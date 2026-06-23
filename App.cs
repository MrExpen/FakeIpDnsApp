using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DnsServerCore.ApplicationCommon;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace FakeIpRouting;

public class FakeIpRoutingApp : IDnsApplication, IDnsPostProcessor
{
    private static readonly JsonDocumentOptions JsonParseOptions = new()
        { CommentHandling = JsonCommentHandling.Skip };

    private bool EnabledIpv4;
    private IPAddressRange IpV4Range = null!;
    private long IpV4Counter;

    private bool EnabledIpv6;
    private IPAddressRange IpV6Range = null!;
    private long IpV6Counter;

    private bool EnabledHttps;

    private bool IgnoreCase;

    private string NftTableNameIpv4 = null!;
    private string NftTableNameIpv6 = null!;
    private string NftMapNameIpv4 = null!;
    private string NftMapNameIpv6 = null!;

    private uint Ttl;
    private FrozenSet<string> Domains = null!;
    private FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup;

    public Task InitializeAsync(IDnsServer dnsServer, string? config)
    {
        Interlocked.Exchange(ref IpV4Counter, 0);
        Interlocked.Exchange(ref IpV6Counter, 0);

        using var jsonDocument = JsonDocument.Parse(config!, JsonParseOptions);
        var jsonConfig = jsonDocument.RootElement;

        EnabledIpv4 = jsonConfig.GetPropertyValue("enabledIpv4", false);
        IpV4Range = IPAddressRange.Parse(jsonConfig.GetPropertyValue("ipV4Range", "198.18.0.0/15"));

        EnabledIpv6 = jsonConfig.GetPropertyValue("enabledIpv6", false);
        IpV6Range = IPAddressRange.Parse(jsonConfig.GetPropertyValue("ipV6Range", "fc00::/106"));

        EnabledHttps = jsonConfig.GetPropertyValue("enabledHttps", true);

        NftTableNameIpv4 = jsonConfig.GetPropertyValue("nftTableNameIpv4", "vpn");
        NftTableNameIpv6 = jsonConfig.GetPropertyValue("nftTableNameIpv6", "vpn");

        NftMapNameIpv4 = jsonConfig.GetPropertyValue("nftMapNameIpv4", "fakeip_map");
        NftMapNameIpv6 = jsonConfig.GetPropertyValue("nftMapNameIpv6", "fakeip6_map");

        Ttl = jsonConfig.GetPropertyValue("ttl", 3600u);

        IgnoreCase = jsonConfig.GetPropertyValue("ignoreCase", true);
        var domains = jsonConfig.ReadArray("domains");

        Domains = IgnoreCase
            ? domains.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : domains.ToFrozenSet();

        Lookup = Domains.GetAlternateLookup<ReadOnlySpan<char>>();

        return Task.CompletedTask;
    }

    public async Task<DnsDatagram?> PostProcessAsync(
        DnsDatagram request,
        IPEndPoint remoteEp,
        DnsTransportProtocol protocol,
        DnsDatagram response
    )
    {
        if (!EnabledIpv4 && !EnabledIpv6)
        {
            return response;
        }

        var question = request.Question[0];
        var domain = question.Name;

        if (response.RCODE != DnsResponseCode.NoError ||
            response.Answer.Count == 0 ||
            question.Type is
                not DnsResourceRecordType.A and
                not DnsResourceRecordType.AAAA and
                not DnsResourceRecordType.HTTPS ||
            (question.Type == DnsResourceRecordType.A && !EnabledIpv4) ||
            (question.Type == DnsResourceRecordType.AAAA && !EnabledIpv6) ||
            !RuleExists(domain))
        {
            return response;
        }

        var originalAnswer = response.Answer[0];
        DnsResourceRecord answer;

        try
        {
            switch (question.Type)
            {
                case DnsResourceRecordType.A when EnabledIpv4 && originalAnswer.RDATA is DnsARecordData ipv4RecordData:
                    answer = new DnsResourceRecord(
                        domain,
                        DnsResourceRecordType.A,
                        DnsClass.IN,
                        Ttl,
                        new DnsARecordData(await AllocateAndMapIpV4Address(ipv4RecordData.Address)));
                    break;

                case DnsResourceRecordType.AAAA
                    when EnabledIpv6 && originalAnswer.RDATA is DnsARecordData ipv6RecordData:
                    answer = new DnsResourceRecord(
                        domain,
                        DnsResourceRecordType.AAAA,
                        DnsClass.IN,
                        Ttl,
                        new DnsARecordData(await AllocateAndMapIpV6Address(ipv6RecordData.Address)));
                    break;

                case DnsResourceRecordType.HTTPS
                    when EnabledHttps && originalAnswer.RDATA is DnsSVCBRecordData httpsRecord &&
                         (httpsRecord.SvcParams.ContainsKey(DnsSvcParamKey.IPv4Hint) ||
                          httpsRecord.SvcParams.ContainsKey(DnsSvcParamKey.IPv6Hint)):
                    answer = new DnsResourceRecord(
                        domain,
                        DnsResourceRecordType.HTTPS,
                        DnsClass.IN,
                        Ttl,
                        new DnsSVCBRecordData(httpsRecord.SvcPriority, httpsRecord.TargetName,
                            httpsRecord.SvcParams
                                .Where(x => x.Key != DnsSvcParamKey.IPv4Hint && x.Key != DnsSvcParamKey.IPv6Hint)
                                .ToDictionary()));

                    break;

                default: return response;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);

            return response;
        }


        return new DnsDatagram(response.Identifier, response.IsResponse, response.OPCODE,
            response.AuthoritativeAnswer, response.Truncation, response.RecursionDesired, response.RecursionAvailable,
            response.AuthenticData, response.CheckingDisabled, response.RCODE, response.Question, [answer],
            response.Authority, response.Additional);
    }

    private bool RuleExists(ReadOnlySpan<char> domain)
    {
        int indexOfDot = domain.Length;
        do
        {
            indexOfDot = domain[..indexOfDot].LastIndexOf('.');
            var currentDomain = domain[(indexOfDot + 1)..];

            if (Lookup.Contains(currentDomain))
            {
                return true;
            }
        } while (indexOfDot >= 0);

        return false;
    }

    private static IPAddress AllocateAddress(IPAddressRange range, ref long counter)
    {
        var addressIndex = Interlocked.Increment(ref counter) - 1;
        addressIndex %= range.AddressCount;

        if (addressIndex < 0)
        {
            addressIndex += range.AddressCount;
        }

        return Add(range.Begin, addressIndex);
    }

    private static IPAddress Add(IPAddress address, long value)
    {
        const int maxNeededBufferLength = 17;
        Span<byte> buffer = stackalloc byte[maxNeededBufferLength];

        if (!address.TryWriteBytes(buffer, out var written))
        {
            throw new InvalidOperationException("Недостижимая ошибка.");
        }

        // TODO optimize path for 32 and 128 bit
        var ipAsInteger = new BigInteger(buffer[..written], isBigEndian: true) + value;
        return !ipAsInteger.TryWriteBytes(buffer, out _, isBigEndian: true)
            ? throw new InvalidOperationException("Недостижимая ошибка.")
            : new IPAddress(buffer[..written]);
    }

    private async Task<IPAddress> AllocateAndMapIpV4Address(
        IPAddress readIp,
        CancellationToken cancellationToken = default
    )
    {
        var address = AllocateAddress(IpV4Range, ref IpV4Counter);

        await MapFakeToRealIpAsync(address, readIp, cancellationToken);

        return address;
    }

    private async Task<IPAddress> AllocateAndMapIpV6Address(
        IPAddress readIp,
        CancellationToken cancellationToken = default
    )
    {
        var address = AllocateAddress(IpV6Range, ref IpV6Counter);

        await MapFakeToRealIpAsync(address, readIp, cancellationToken);

        return address;
    }

    private async Task MapFakeToRealIpAsync(
        IPAddress fake,
        IPAddress real,
        CancellationToken cancellationToken = default
    )
    {
        if (fake.AddressFamily != real.AddressFamily)
        {
            throw new InvalidOperationException($"couldn't map {fake.AddressFamily} address with {real.AddressFamily}");
        }

        IEnumerable<string> arguments = real.AddressFamily switch
        {
            // TODO: ttl
            AddressFamily.InterNetwork =>
                ["add", "element", "ip", NftTableNameIpv4, NftMapNameIpv4, $"{{ {fake} : {real} }}"],

            // TODO: ttl
            AddressFamily.InterNetworkV6 =>
                ["add", "element", "ip6", NftTableNameIpv6, NftMapNameIpv6, $"{{ {fake} : {real} }}"],

            _ => throw new InvalidOperationException($"address family {real.AddressFamily} not supported")
        };

        using var process = Process.Start("nft", arguments);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("couldn't map fake ip address");
        }
    }

    public void Dispose()
    {
        // Ignore
    }

    public string Description => "Allocate ip for resources in fake range.";
}
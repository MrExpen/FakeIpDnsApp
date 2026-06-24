using System;
using System.Collections.Frozen;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DnsServerCore.ApplicationCommon;
using Google.Protobuf;
using Grpc.Net.Client;
using IpGrpcMap;
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

    private string GrpcAgentAddress = null!;
    private GrpcChannel? Channel;
    private IpMapper.IpMapperClient IpMapper = null!;

    private bool IgnoreCase;
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

        GrpcAgentAddress = jsonConfig.GetPropertyValue("grpcAgentAddress", "http://127.0.0.1:5000");
        Channel?.Dispose();
        Channel = GrpcChannel.ForAddress(GrpcAgentAddress);
        IpMapper = new IpMapper.IpMapperClient(Channel);

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

        DnsResourceRecord answer;

        try
        {
            switch (question.Type)
            {
                case DnsResourceRecordType.A when EnabledIpv4 && response.Answer.Select(x => x.RDATA).OfType<DnsARecordData>().FirstOrDefault() is { } ipv4RecordData:
                    answer = new DnsResourceRecord(
                        domain,
                        DnsResourceRecordType.A,
                        DnsClass.IN,
                        Ttl,
                        new DnsARecordData(await AllocateAndMapIpV4Address(ipv4RecordData.Address, Ttl)));
                    break;

                case DnsResourceRecordType.AAAA
                    when EnabledIpv6 && response.Answer.Select(x => x.RDATA).OfType<DnsAAAARecordData>().FirstOrDefault() is { } ipv6RecordData:
                    answer = new DnsResourceRecord(
                        domain,
                        DnsResourceRecordType.AAAA,
                        DnsClass.IN,
                        Ttl,
                        new DnsARecordData(await AllocateAndMapIpV6Address(ipv6RecordData.Address, Ttl)));
                    break;

                case DnsResourceRecordType.HTTPS
                    when EnabledHttps && response.Answer[0].RDATA is DnsSVCBRecordData httpsRecord &&
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
        uint ttl,
        CancellationToken cancellationToken = default
    )
    {
        var address = AllocateAddress(IpV4Range, ref IpV4Counter);

        await MapFakeToRealIpAsync(address, readIp, ttl, cancellationToken);

        return address;
    }

    private async Task<IPAddress> AllocateAndMapIpV6Address(
        IPAddress readIp,
        uint ttl,
        CancellationToken cancellationToken = default
    )
    {
        var address = AllocateAddress(IpV6Range, ref IpV6Counter);

        await MapFakeToRealIpAsync(address, readIp, ttl, cancellationToken);

        return address;
    }

    private async Task MapFakeToRealIpAsync(
        IPAddress fake,
        IPAddress real,
        uint ttl,
        CancellationToken cancellationToken = default
    )
    {
        if (fake.AddressFamily != real.AddressFamily)
        {
            throw new InvalidOperationException($"couldn't map {fake.AddressFamily} address with {real.AddressFamily}");
        }

        await IpMapper.MapIpAsync(
            new MapIpRequest { FakeIp = ToByteString(fake), RealIp = ToByteString(real), Ttl = ttl },
            cancellationToken: cancellationToken);
    }

    private ByteString ToByteString(IPAddress address)
    {
        Span<byte> buffer = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => stackalloc byte[4],
            AddressFamily.InterNetworkV6 => stackalloc byte[16],
            _ => throw new ArgumentOutOfRangeException()
        };

        if (!address.TryWriteBytes(buffer, out var written))
        {
            throw new InvalidOperationException();
        }

        return ByteString.CopyFrom(buffer);
    }

    public void Dispose()
    {
        Channel?.Dispose();
    }

    public string Description => "Allocate ip for resources in fake range.";
}
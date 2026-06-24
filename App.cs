using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DnsServerCore.ApplicationCommon;
using FakeIpRouting.Extensions;
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

    private bool Disable;

    private string GrpcAgentAddress = null!;
    private GrpcChannel? Channel;
    private IpMapper.IpMapperClient IpMapper = null!;

    private bool IgnoreCase;
    private uint Ttl;

    private FrozenDictionary<string, int> DomainToRange = null!;
    private FrozenDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> Lookup;

    private List<RangeConfig> Configs = null!;

    public Task InitializeAsync(IDnsServer dnsServer, string? config)
    {
        using var jsonDocument = JsonDocument.Parse(config!, JsonParseOptions);
        var jsonConfig = jsonDocument.RootElement;

        Disable = jsonConfig.GetPropertyValue("disable", false);

        GrpcAgentAddress = jsonConfig.GetPropertyValue("grpcAgentAddress", "http://127.0.0.1:5000");
        Channel?.Dispose();
        Channel = GrpcChannel.ForAddress(GrpcAgentAddress);
        IpMapper = new IpMapper.IpMapperClient(Channel);

        Ttl = jsonConfig.GetPropertyValue("ttl", 3600u);
        IgnoreCase = jsonConfig.GetPropertyValue("ignoreCase", true);

        var configs = new List<RangeConfig>();
        var dictionary = IgnoreCase
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>();

        foreach (var rangeConfig in jsonConfig.GetProperty("configs").EnumerateArray())
        {
            if (rangeConfig.GetPropertyValue("disabled", false))
            {
                continue;
            }

            var parsedConfig = new RangeConfig(
                rangeConfig.GetPropertyValue("ipV4Range", null),
                rangeConfig.GetPropertyValue("ipV6Range", null)
            );

            if (parsedConfig is { Ipv4Configured: false, Ipv6Configured: false })
            {
                continue;
            }

            var domains = rangeConfig.ReadArray("domains");

            if (domains is null || domains.Length == 0)
            {
                continue;
            }

            configs.Add(parsedConfig);

            foreach (var domain in domains)
            {
                dictionary.TryAdd(domain, configs.Count - 1);
            }
        }

        Configs = configs;

        DomainToRange = IgnoreCase
            ? dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            : dictionary.ToFrozenDictionary();

        Lookup = DomainToRange.GetAlternateLookup<ReadOnlySpan<char>>();

        return Task.CompletedTask;
    }

    public async Task<DnsDatagram?> PostProcessAsync(
        DnsDatagram request,
        IPEndPoint remoteEp,
        DnsTransportProtocol protocol,
        DnsDatagram response
    )
    {
        if (Disable)
        {
            return response;
        }

        var question = request.Question[0];
        var domain = question.Name;

        if (response.RCODE != DnsResponseCode.NoError ||
            response.Answer.Count == 0 ||
            question.Type is
                not DnsResourceRecordType.A and
                not DnsResourceRecordType.AAAA ||
            !TryGetRangeIndex(domain, out var rangeIndex))
        {
            return response;
        }

        var config = GetRangeConfig(rangeIndex);

        if (config is null)
        {
            return response;
        }

        DnsResourceRecord answer;

        try
        {
            switch (question.Type)
            {
                case DnsResourceRecordType.A when config.Ipv4Configured &&
                                                  response.Answer.Select(x => x.RDATA).OfType<DnsARecordData>()
                                                      .FirstOrDefault() is { } ipv4RecordData:
                    answer = new DnsResourceRecord(
                        domain,
                        DnsResourceRecordType.A,
                        DnsClass.IN,
                        Ttl,
                        new DnsARecordData(await AllocateAndMapIpAddress(config.Ipv4Allocator!, ipv4RecordData.Address,
                            Ttl)));
                    break;

                case DnsResourceRecordType.AAAA
                    when config.Ipv6Configured &&
                         response.Answer.Select(x => x.RDATA).OfType<DnsAAAARecordData>().FirstOrDefault() is
                             { } ipv6RecordData:
                    answer = new DnsResourceRecord(
                        domain,
                        DnsResourceRecordType.AAAA,
                        DnsClass.IN,
                        Ttl,
                        new DnsARecordData(await AllocateAndMapIpAddress(config.Ipv6Allocator!, ipv6RecordData.Address,
                            Ttl)));
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

    private bool TryGetRangeIndex(string domain, out int rangeIndex)
    {
        foreach (var domainLevel in domain.EnumerateDomainLevels())
        {
            if (Lookup.TryGetValue(domainLevel, out rangeIndex))
            {
                return true;
            }
        }

        rangeIndex = -1;
        return false;
    }

    private RangeConfig? GetRangeConfig(int index)
    {
        if (index < 0 || index >= Configs.Count)
        {
            return null;
        }

        return Configs[index];
    }

    private async Task<IPAddress> AllocateAndMapIpAddress(
        IpAllocator allocator,
        IPAddress readIp,
        uint ttl,
        CancellationToken cancellationToken = default
    )
    {
        var address = allocator.Allocate();

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
            new MapIpRequest { FakeIp = fake.ToByteString(), RealIp = real.ToByteString(), Ttl = ttl },
            cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        Channel?.Dispose();
    }

    public string Description => "Allocate ip for resources in fake range.";
}
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    private IDnsServer DnsServer = null!;

    private bool Disable;
    private bool Cache;

    private string GrpcAgentAddress = null!;
    private GrpcChannel? Channel;
    private IpMapper.IpMapperClient IpMapper = null!;

    private bool IgnoreCase;
    private uint MaxTtl;

    private FrozenDictionary<string, int> DomainToRange = null!;
    private FrozenDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> Lookup;

    private List<RangeConfig> Configs = null!;

    public Task InitializeAsync(IDnsServer dnsServer, string? config)
    {
        DnsServer = dnsServer;

        using var jsonDocument = JsonDocument.Parse(config!, JsonParseOptions);
        var jsonConfig = jsonDocument.RootElement;

        var allocatorFactory = new IpAllocatorFactory();

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Configs is not null)
        {
            foreach (var rangeConfig in Configs)
            {
                allocatorFactory.TryAdd(rangeConfig.Ipv4Allocator);
                allocatorFactory.TryAdd(rangeConfig.Ipv6Allocator);
            }
        }

        Disable = jsonConfig.GetPropertyValue("disable", false);
        Cache = jsonConfig.GetPropertyValue("cache", true);

        GrpcAgentAddress = jsonConfig.GetPropertyValue("grpcAgentAddress", "http://127.0.0.1:5000");
        Channel?.Dispose();
        Channel = GrpcChannel.ForAddress(GrpcAgentAddress);
        IpMapper = new IpMapper.IpMapperClient(Channel);

        MaxTtl = jsonConfig.GetPropertyValue("maxTtl", 3600u);
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
                allocatorFactory.Create(rangeConfig.GetPropertyValue("ipV4Range", null)),
                allocatorFactory.Create(rangeConfig.GetPropertyValue("ipV6Range", null)),
                rangeConfig.GetPropertyValue("maxIpv4AddressesPerDomain", 1),
                rangeConfig.GetPropertyValue("maxIpv6AddressesPerDomain", 1)
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
        try
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
                !TryGetRangeConfig(domain, out var config))
            {
                return response;
            }
            
            IReadOnlyList<DnsResourceRecord> answers;

            switch (question.Type)
            {
                // If response is already FakeIp range. Cache path
                case DnsResourceRecordType.A when config.Ipv4Configured &&
                                                  response.Answer
                                                      .Select(x => x.RDATA)
                                                      .OfType<DnsARecordData>()
                                                      .Any(x => config.Ipv4Allocator!.RangeContains(x.Address)):
                    return response;
                // If response is already FakeIp range. Cache path
                case DnsResourceRecordType.AAAA when config.Ipv6Configured &&
                                                     response.Answer
                                                         .Select(x => x.RDATA)
                                                         .OfType<DnsAAAARecordData>()
                                                         .Any(x => config.Ipv6Allocator!.RangeContains(x.Address)):
                    return response;

                case DnsResourceRecordType.A when config.Ipv4Configured:
                    var aRecords = response.Answer
                        .Select(x => new { Record = x.RDATA as DnsARecordData, Ttl = x.TTL })
                        .Where(x => x.Record is not null)
                        .TakeIfNotZero(config.MaxIpv4AddressesPerDomain)
                        .ToArray();

                    var fakeIpv4 = await AllocateAndMapIpAddress(
                        config.Ipv4Allocator!,
                        aRecords.Select(x => x.Record!.Address),
                        MaxTtl
                    );

                    answers =
                    [
                        .. fakeIpv4.Zip(aRecords)
                            .Select(zip => new DnsResourceRecord(
                                domain,
                                DnsResourceRecordType.A,
                                DnsClass.IN,
                                Math.Min(zip.Second.Ttl, MaxTtl),
                                new DnsARecordData(zip.First)))
                    ];

                    break;

                case DnsResourceRecordType.AAAA when config.Ipv6Configured:
                    var aaaaRecords = response.Answer
                        .Select(x => new { Record = x.RDATA as DnsAAAARecordData, Ttl = x.TTL })
                        .Where(x => x.Record is not null)
                        .TakeIfNotZero(config.MaxIpv6AddressesPerDomain)
                        .ToArray();

                    var fakeIpv6 = await AllocateAndMapIpAddress(
                        config.Ipv6Allocator!,
                        aaaaRecords.Select(x => x.Record!.Address),
                        MaxTtl
                    );

                    answers =
                    [
                        .. fakeIpv6.Zip(aaaaRecords)
                            .Select(zip => new DnsResourceRecord(
                                domain,
                                DnsResourceRecordType.AAAA,
                                DnsClass.IN,
                                Math.Min(zip.Second.Ttl, MaxTtl),
                                new DnsAAAARecordData(zip.First)))
                    ];

                    break;

                default: return response;
            }


            var result = new DnsDatagram(response.Identifier, response.IsResponse, response.OPCODE,
                response.AuthoritativeAnswer, response.Truncation, response.RecursionDesired,
                response.RecursionAvailable,
                response.AuthenticData, response.CheckingDisabled, response.RCODE, response.Question, answers,
                response.Authority, response.Additional);

            if (Cache)
            {
                DnsServer.DnsCache.CacheResponse(result);
            }

            return result;
        }
        catch (Exception e)
        {
            DnsServer.WriteLog(e);

            return response;
        }
    }

    private bool TryGetRangeConfig(string domain, [MaybeNullWhen(false)] out RangeConfig rangeConfig)
    {
        foreach (var domainLevel in domain.EnumerateDomainLevels())
        {
            if (Lookup.TryGetValue(domainLevel, out var rangeIndex))
            {
                if (rangeIndex < 0 || rangeIndex >= Configs.Count)
                {
                    rangeConfig = null;
                    return false;
                }

                rangeConfig = Configs[rangeIndex];
                return true;
            }
        }

        rangeConfig = null;
        return false;
    }

    private async Task<IPAddress[]> AllocateAndMapIpAddress(
        IpAllocator allocator,
        IEnumerable<IPAddress> readIp,
        uint ttl,
        CancellationToken cancellationToken = default
    )
    {
        var realIpArray = readIp.ToArray();
        var address = allocator.Allocate(realIpArray.Length);

        await MapFakeToRealIpAsync(address, realIpArray, ttl, cancellationToken);

        return address;
    }

    private async Task MapFakeToRealIpAsync(
        IEnumerable<IPAddress> fake,
        IEnumerable<IPAddress> real,
        uint ttl,
        CancellationToken cancellationToken = default
    )
    {
        await IpMapper.MapIpMultipleAsync(
            new MapIpMultipleRequest
            {
                FakeIps = { fake.Select(x => x.ToByteString()) },
                RealIps = { real.Select(x => x.ToByteString()) },
                Ttl = ttl
            },
            cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        Channel?.Dispose();
    }

    public string Description => "Allocate ip for resources in fake range.";
}
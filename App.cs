using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DnsServerCore.ApplicationCommon;
using FakeIpRouting.Allocators;
using FakeIpRouting.Extensions;
using FakeIpRouting.Services;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace FakeIpRouting;

public sealed class FakeIpRoutingApp : IDnsApplication, IDnsPostProcessor
{
    private static readonly JsonDocumentOptions JsonParseOptions = new()
        { CommentHandling = JsonCommentHandling.Skip };

    private IDnsServer DnsServer = null!;

    private bool Disable;
    private bool Cache;

    private IIpMap? IpMap;

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

        var ipv4AllocatorFactory = new Ipv4AllocatorFactory();
        var ipv6AllocatorFactory = new Ipv6AllocatorFactory();

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Configs is not null)
        {
            foreach (var rangeConfig in Configs)
            {
                ipv4AllocatorFactory.TryAdd(rangeConfig.Ipv4Allocator);
                ipv6AllocatorFactory.TryAdd(rangeConfig.Ipv6Allocator);
            }
        }

        Disable = jsonConfig.GetPropertyValue("disable", false);
        Cache = jsonConfig.GetPropertyValue("cache", true);

        IpMap?.Dispose();

        var grpcAgentAddress = jsonConfig.GetPropertyValue("grpcAgentAddress", null);
        if (!string.IsNullOrWhiteSpace(grpcAgentAddress))
        {
            IpMap = GrpcIpMap.Create(grpcAgentAddress);
        }
        else
        {
            IpMap = new LocalNftIpMap(
                jsonConfig.GetPropertyValue("logging", false) ? new DnsServerLogger(dnsServer) : NullLogger.Instance,
                jsonConfig.GetPropertyValue("ipv4TableName", "vpn"),
                jsonConfig.GetPropertyValue("ipv6TableName", "vpn"),
                jsonConfig.GetPropertyValue("ipv4MapName", "fakeip_map"),
                jsonConfig.GetPropertyValue("ipv6MapName", "fakeip6_map")
            );
        }

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
                ipv4AllocatorFactory.Create(rangeConfig.GetPropertyValue("ipV4Range", null)),
                ipv6AllocatorFactory.Create(rangeConfig.GetPropertyValue("ipV6Range", null)),
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
        if (Disable)
        {
            return response;
        }

        var question = request.Question[0];
        var domain = question.Name;

        if (question.Class != DnsClass.IN ||
            response.OPCODE != DnsOpcode.StandardQuery ||
            response.RCODE != DnsResponseCode.NoError ||
            response.Answer.Count == 0 ||
            question.Type is
                not DnsResourceRecordType.A and
                not DnsResourceRecordType.AAAA ||
            !TryGetRangeConfig(domain, out var config))
        {
            return response;
        }

        IReadOnlyList<DnsResourceRecord> answers;

        // Hack for CNAME
        if (Cache && response.Answer.Any(x => x.RDATA is DnsCNAMERecordData))
        {
            var allCachedRecords = await DnsServer.DirectQueryAsync(
                new DnsQuestionRecord(domain, DnsResourceRecordType.ANY, question.Class)
            );

            var answersFromCache = allCachedRecords.Answer.Where(x => x.Type == question.Type).ToArray();

            var isIpv4FromFakeRange = answersFromCache.Length != 0 &&
                                      question.Type == DnsResourceRecordType.A &&
                                      config.Ipv4Configured &&
                                      answersFromCache.Any(x =>
                                          config.Ipv4Allocator!.RangeContains((x.RDATA as DnsARecordData)!
                                              .Address));

            var isIpv6FromFakeRange = answersFromCache.Length != 0 &&
                                      question.Type == DnsResourceRecordType.AAAA &&
                                      config.Ipv6Configured &&
                                      answersFromCache.Any(x =>
                                          config.Ipv6Allocator!.RangeContains((x.RDATA as DnsAAAARecordData)!
                                              .Address));

            if (isIpv4FromFakeRange || isIpv6FromFakeRange)
            {
                return new DnsDatagram(response.Identifier, true, request.OPCODE,
                    false, response.Truncation, request.RecursionDesired,
                    request.RecursionAvailable,
                    false, false, DnsResponseCode.NoError, request.Question,
                    answersFromCache);
            }
        }

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
            response.AuthenticData, response.CheckingDisabled, response.RCODE, response.Question, answers);

        if (Cache)
        {
            DnsServer.DnsCache.CacheResponse(result);
        }

        return result;
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

    private async Task<IPAddress[]> AllocateAndMapIpAddress<TUnit>(
        BaseIpAllocator<TUnit> allocator,
        IEnumerable<IPAddress> readIp,
        uint ttl,
        CancellationToken cancellationToken = default
    ) where TUnit : struct, IBinaryInteger<TUnit>
    {
        var realIpArray = readIp.ToArray();
        var fakeAddresses = allocator.Allocate((uint)realIpArray.Length);

        var mappings = fakeAddresses.Zip(realIpArray)
            .Select(x => new KeyValuePair<IPAddress, IPAddress>(x.First, x.Second))
            .ToArray();

        await IpMap.CreateMappingsAsync(mappings, ttl, cancellationToken);

        return fakeAddresses;
    }

    public void Dispose()
    {
        IpMap?.Dispose();
    }

    public string Description => "Allocate ip for resources in fake range.";
}
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FakeIpRouting.Extensions;
using Grpc.Net.Client;
using IpGrpcMap;

namespace FakeIpRouting.Services;

public sealed class GrpcIpMap : IIpMap
{
    private readonly GrpcChannel Channel;
    private readonly IpMapper.IpMapperClient IpMapper;

    private GrpcIpMap(GrpcChannel channel)
    {
        Channel = channel;
        IpMapper = new IpMapper.IpMapperClient(channel);
    }

    public Task CreateMappingsAsync(
        IReadOnlyCollection<KeyValuePair<IPAddress, IPAddress>> mappings,
        uint ttl,
        CancellationToken cancellationToken
    )
    {
        return IpMapper.MapIpMultipleAsync(new MapIpMultipleRequest
        {
            FakeIps = { mappings.Select(x => x.Key.ToByteString()) },
            RealIps = { mappings.Select(x => x.Value.ToByteString()) },
            Ttl = ttl
        }, cancellationToken: cancellationToken).ResponseAsync;
    }

    public static GrpcIpMap Create(string address)
    {
        return new GrpcIpMap(GrpcChannel.ForAddress(address));
    }

    public void Dispose()
    {
        Channel.Dispose();
    }
}
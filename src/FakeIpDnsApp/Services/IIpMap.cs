using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FakeIpDnsApp.Services;

public interface IIpMap : IDisposable
{
    public Task CreateMappingsAsync(
        IReadOnlyCollection<KeyValuePair<IPAddress, IPAddress>> mappings,
        uint ttl,
        CancellationToken cancellationToken
    );
}
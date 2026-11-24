using System;

namespace FakeIpRouting.Allocators;

public sealed class Ipv6AllocatorFactory : BaseIpAllocatorFactory<Ipv6Allocator, UInt128>
{
    protected override Ipv6Allocator InternalCreate(IPAddressRange range) => new(range);
}
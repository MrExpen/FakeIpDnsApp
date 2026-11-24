namespace FakeIpDnsApp.Allocators;

public sealed class Ipv4AllocatorFactory : BaseIpAllocatorFactory<Ipv4Allocator, uint>
{
    protected override Ipv4Allocator InternalCreate(IPAddressRange range) => new(range);
}
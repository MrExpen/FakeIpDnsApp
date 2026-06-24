namespace FakeIpRouting;

public class RangeConfig
{
    public IpAllocator? Ipv4Allocator { get; }
    public IpAllocator? Ipv6Allocator { get; }

    public bool Ipv4Configured => Ipv4Allocator is not null;
    public bool Ipv6Configured => Ipv6Allocator is not null;

    public RangeConfig(string? ipv4Range, string? ipv6Range)
    {
        if (ipv4Range is not null)
        {
            Ipv4Allocator = new IpAllocator(IPAddressRange.Parse(ipv4Range));
        }

        if (ipv6Range is not null)
        {
            Ipv6Allocator = new IpAllocator(IPAddressRange.Parse(ipv6Range));
        }
    }
}
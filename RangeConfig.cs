namespace FakeIpRouting;

public class RangeConfig
{
    public IpAllocator? Ipv4Allocator { get; }
    public IpAllocator? Ipv6Allocator { get; }

    public int MaxIpv4AddressesPerDomain { get; }
    public int MaxIpv6AddressesPerDomain { get; }

    public bool Ipv4Configured => Ipv4Allocator is not null;
    public bool Ipv6Configured => Ipv6Allocator is not null;

    public RangeConfig(
        string? ipv4Range,
        string? ipv6Range,
        int maxIpv4AddressesPerDomain,
        int maxIpv6AddressesPerDomain
    )
    {
        if (ipv4Range is not null)
        {
            Ipv4Allocator = IpAllocator.ParseRange(ipv4Range);
        }

        if (ipv6Range is not null)
        {
            Ipv6Allocator = IpAllocator.ParseRange(ipv6Range);
        }

        MaxIpv4AddressesPerDomain = maxIpv4AddressesPerDomain;
        MaxIpv6AddressesPerDomain = maxIpv6AddressesPerDomain;
    }

    public RangeConfig(
        IpAllocator? ipv4Allocator,
        IpAllocator? ipv6Allocator,
        int maxIpv4AddressesPerDomain,
        int maxIpv6AddressesPerDomain)
    {
        Ipv4Allocator = ipv4Allocator;
        Ipv6Allocator = ipv6Allocator;
        MaxIpv4AddressesPerDomain = maxIpv4AddressesPerDomain;
        MaxIpv6AddressesPerDomain = maxIpv6AddressesPerDomain;
    }
}
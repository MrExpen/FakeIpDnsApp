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
            Ipv4Allocator = new IpAllocator(IPAddressRange.Parse(ipv4Range));
        }

        if (ipv6Range is not null)
        {
            Ipv6Allocator = new IpAllocator(IPAddressRange.Parse(ipv6Range));
        }
        
        MaxIpv4AddressesPerDomain = maxIpv4AddressesPerDomain;
        MaxIpv6AddressesPerDomain = maxIpv6AddressesPerDomain;
    }
}
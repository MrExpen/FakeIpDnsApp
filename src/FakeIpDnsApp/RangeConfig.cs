using FakeIpDnsApp.Allocators;

namespace FakeIpDnsApp;

public class RangeConfig
{
    public Ipv4Allocator? Ipv4Allocator { get; }
    public Ipv6Allocator? Ipv6Allocator { get; }

    public int MaxIpv4AddressesPerDomain { get; }
    public int MaxIpv6AddressesPerDomain { get; }

    public bool Ipv4Configured => Ipv4Allocator is not null;
    public bool Ipv6Configured => Ipv6Allocator is not null;

    public RangeConfig(
        Ipv4Allocator? ipv4Allocator,
        Ipv6Allocator? ipv6Allocator,
        int maxIpv4AddressesPerDomain,
        int maxIpv6AddressesPerDomain)
    {
        Ipv4Allocator = ipv4Allocator;
        Ipv6Allocator = ipv6Allocator;
        MaxIpv4AddressesPerDomain = maxIpv4AddressesPerDomain;
        MaxIpv6AddressesPerDomain = maxIpv6AddressesPerDomain;
    }
}
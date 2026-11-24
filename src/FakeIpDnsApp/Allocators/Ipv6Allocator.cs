using System;
using System.Net;
using System.Net.Sockets;

namespace FakeIpDnsApp.Allocators;

public sealed class Ipv6Allocator : BaseIpAllocator<UInt128>
{
    public Ipv6Allocator(IPAddressRange range) : base(range)
    {
        if (range.Begin.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException();
        }
    }

    public override bool RangeContains(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6 && base.RangeContains(address);
    }
}
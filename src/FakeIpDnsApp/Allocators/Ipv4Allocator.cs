using System;
using System.Net;
using System.Net.Sockets;

namespace FakeIpDnsApp.Allocators;

public sealed class Ipv4Allocator : BaseIpAllocator<uint>
{
    public Ipv4Allocator(IPAddressRange range) : base(range)
    {
        if (range.Begin.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException();
        }
    }

    public override bool RangeContains(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork && base.RangeContains(address);
    }
}
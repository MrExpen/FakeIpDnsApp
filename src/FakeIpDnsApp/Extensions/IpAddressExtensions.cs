using System;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

namespace FakeIpRouting.Extensions;

public static class IpAddressExtensions
{
    extension(IPAddress ip)
    {
        public int BufferSize => ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => 4,
            AddressFamily.InterNetworkV6 => 16,
            _ => throw new NotSupportedException()
        };

        public ByteString ToByteString()
        {
            Span<byte> buffer = stackalloc byte[ip.BufferSize];

            if (!ip.TryWriteBytes(buffer, out var written))
            {
                throw new InvalidOperationException();
            }

            return ByteString.CopyFrom(buffer);
        }
    }
}
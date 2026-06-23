using System;
using System.Net;
using System.Net.Sockets;

namespace FakeIpRouting;

public class IPAddressRange
{
    public IPAddress Begin { get; }

    public long AddressCount { get; }

    private IPAddressRange(IPAddress begin, long addressCount)
    {
        Begin = begin;
        AddressCount = addressCount;
    }

    public static IPAddressRange Parse(ReadOnlySpan<char> value)
    {
        var indexOf = value.IndexOf('/');

        var ip = IPAddress.Parse(value[..indexOf]);
        var hostLength = int.Parse(value[(indexOf + 1)..]);

        var addressLength = ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => throw new InvalidOperationException()
        };

        var maskLength = addressLength - hostLength;

        if (hostLength > addressLength)
        {
            throw new FormatException();
        }

        Span<byte> buffer = stackalloc byte[addressLength / 8];

        if (!ip.TryWriteBytes(buffer, out var written) || written != addressLength / 8)
        {
            throw new InvalidOperationException("Недостижимая ошибка");
        }

        var index = 1;

        while (maskLength > 8)
        {
            buffer[^index++] = 0;
            maskLength -= 8;
        }

        buffer[^index] &= (byte)(0b11111111 << maskLength);

        return new IPAddressRange(new IPAddress(buffer), 1L << (addressLength - hostLength));
    }
}
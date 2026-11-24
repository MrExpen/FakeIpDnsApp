using System;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using FakeIpDnsApp.Extensions;

namespace FakeIpDnsApp;

public class IPAddressRange : IEquatable<IPAddressRange>
{
    public IPAddress Begin { get; }

    public BigInteger AddressCount { get; }

    internal IPAddressRange(IPAddress begin, BigInteger addressCount)
    {
        Begin = begin;
        AddressCount = addressCount;
    }

    public static IPAddressRange Parse(ReadOnlySpan<char> value)
    {
        var indexOfSeparator = value.IndexOfAny("-/");

        if (indexOfSeparator < 0)
        {
            throw new FormatException();
        }

        return value[indexOfSeparator] switch
        {
            '/' => ParseCidr(indexOfSeparator, value),
            '-' => ParseRange(indexOfSeparator, value),
            _ => throw new FormatException()
        };
    }

    /// <summary>
    /// <example>198.18.0.0/15</example>
    /// <example>2001:db8::/96</example>
    /// </summary>
    private static IPAddressRange ParseCidr(int indexOfSeparator, ReadOnlySpan<char> value)
    {
        var ip = IPAddress.Parse(value[..indexOfSeparator]);
        var hostLength = int.Parse(value[(indexOfSeparator + 1)..]);

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

        return new IPAddressRange(new IPAddress(buffer), BigInteger.One << (addressLength - hostLength));
    }

    /// <summary>
    /// <example>198.18.0.0-198.19.255.255</example>
    /// <example>2001:db8::-2001:db8::ffff:ffff</example>
    /// </summary>
    private static IPAddressRange ParseRange(int indexOfSeparator, ReadOnlySpan<char> value)
    {
        var begin = IPAddress.Parse(value[..indexOfSeparator]);
        var end = IPAddress.Parse(value[(indexOfSeparator + 1)..]);

        if (begin.AddressFamily != end.AddressFamily)
        {
            throw new InvalidOperationException();
        }

        Span<byte> buffer = stackalloc byte[begin.BufferSize];

        begin.TryWriteBytes(buffer, out _);

        var beginInteger = new BigInteger(buffer, isBigEndian: true);

        end.TryWriteBytes(buffer, out _);

        var endInteger = new BigInteger(buffer, isBigEndian: true);

        if (endInteger < beginInteger)
        {
            throw new InvalidOperationException();
        }

        return new IPAddressRange(begin, endInteger - beginInteger + BigInteger.One);
    }

    public bool Equals(IPAddressRange? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Begin.Equals(other.Begin) && AddressCount == other.AddressCount;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((IPAddressRange)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Begin, AddressCount);
    }
}
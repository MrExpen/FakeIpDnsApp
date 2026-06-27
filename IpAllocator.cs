using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Numerics;
using System.Threading;
using FakeIpRouting.Extensions;

namespace FakeIpRouting;

public sealed class IpAllocator
{
    public IPAddressRange Range
    {
        get
        {
            Span<byte> buffer = stackalloc byte[BufferSize];
            RangeStart.TryWriteBytes(buffer, out _, isBigEndian: true);
            return new IPAddressRange(new IPAddress(buffer), RangeSize);
        }
    }

    private readonly BigInteger RangeStart;
    private readonly long RangeSize;
    private readonly int BufferSize;

    private long Counter;

    public IpAllocator(IPAddressRange range)
    {
        BufferSize = range.Begin.BufferSize;
        RangeStart = range.Begin.ToBigInteger();
        RangeSize = range.AddressCount;
        Counter = 0L;
    }

    public IPAddress Allocate()
    {
        var addressIndex = Interlocked.Increment(ref Counter) - 1;

        return GetAddressByIndex(addressIndex);
    }

    public IPAddress[] Allocate(int count)
    {
        var lastIndex = Interlocked.Add(ref Counter, count) - 1;

        var result = new IPAddress[count];

        for (int i = 0; i < count; i++)
        {
            result[i] = GetAddressByIndex(lastIndex - i);
        }

        return result;
    }

    public bool RangeContains(IPAddress address)
    {
        if (address.BufferSize != BufferSize)
        {
            return false;
        }

        var distance = address.ToBigInteger() - RangeStart;

        return distance >= 0 && distance < RangeSize;
    }

    private IPAddress GetAddressByIndex(long index)
    {
        index %= RangeSize;

        if (index < 0)
        {
            index += RangeSize;
        }

        var newAddressInteger = RangeStart + index;

        return newAddressInteger.ToIpAddress(BufferSize);
    }

    public static IpAllocator ParseRange(ReadOnlySpan<char> range)
    {
        return new IpAllocator(IPAddressRange.Parse(range));
    }

    private sealed class RangeSizeRangeStartEqualityComparer : IEqualityComparer<IpAllocator>
    {
        public bool Equals(IpAllocator? x, IpAllocator? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null) return false;
            if (y is null) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.RangeSize == y.RangeSize && x.RangeStart.Equals(y.RangeStart);
        }

        public int GetHashCode(IpAllocator obj)
        {
            return HashCode.Combine(obj.RangeSize, obj.RangeStart);
        }
    }

    public static IEqualityComparer<IpAllocator> RangeComparer { get; } = new RangeSizeRangeStartEqualityComparer();
}
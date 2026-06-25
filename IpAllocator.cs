using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using System.Threading;
using FakeIpRouting.Extensions;

namespace FakeIpRouting;

public class IpAllocator
{
    private readonly BigInteger RangeStart;
    private readonly long RangeSize;
    private readonly int BufferSize;

    private long Counter;

    public IpAllocator(IPAddressRange range)
    {
        BufferSize = range.Begin.BufferSize;

        Span<byte> buffer = stackalloc byte[BufferSize];

        if (!range.Begin.TryWriteBytes(buffer, out var written))
        {
            throw new InvalidOperationException("Недостижимая ошибка.");
        }

        RangeStart = new BigInteger(buffer[..written], isBigEndian: true);
        RangeSize = range.AddressCount;
        Counter = 0L;
    }

    public IPAddress Allocate()
    {
        var addressIndex = Interlocked.Increment(ref Counter) - 1;

        return GetAddressByIndex(addressIndex);
    }

    public IEnumerable<IPAddress> Allocate(int count)
    {
        var lastIndex = Interlocked.Add(ref Counter, count) - 1;

        for (int i = 0; i < count; i++)
        {
            yield return GetAddressByIndex(lastIndex - i);
        }
    }

    private IPAddress GetAddressByIndex(long index)
    {
        index %= RangeSize;

        if (index < 0)
        {
            index += RangeSize;
        }

        Span<byte> buffer = stackalloc byte[BufferSize];

        var newAddress = RangeStart + index;

        newAddress.TryWriteBytes(buffer, out _, isBigEndian: true);

        return new IPAddress(buffer);
    }
}
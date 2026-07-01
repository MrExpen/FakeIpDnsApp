using System;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace FakeIpRouting.Allocators;

public abstract class BaseIpAllocator<TUint> where TUint : struct, IBinaryInteger<TUint>
{
    public IPAddressRange Range => new(IntToAddress(RangeStart), RangeSize);

    private readonly TUint RangeStart;
    private readonly uint RangeSize;

    private volatile uint Counter;

    protected BaseIpAllocator(IPAddressRange range)
    {
        RangeStart = AddressToInt(range.Begin);
        checked
        {
            RangeSize = (uint)range.AddressCount;
        }

        Counter = 0U;
    }

    public IPAddress Allocate()
    {
        var addressIndex = Interlocked.Increment(ref Counter) - 1;

        return GetAddressByIndex(addressIndex);
    }

    public IPAddress[] Allocate(uint count)
    {
        if (count == 0)
        {
            return [];
        }

        var lastIndex = Interlocked.Add(ref Counter, count) - 1;

        var result = new IPAddress[count];

        for (uint i = 0; i < count; i++)
        {
            result[i] = GetAddressByIndex(lastIndex - i);
        }

        return result;
    }

    public virtual bool RangeContains(IPAddress address)
    {
        var addressInt = AddressToInt(address);

        return RangeStart <= addressInt && addressInt - RangeStart < TUint.CreateTruncating(RangeSize);
    }

    private IPAddress GetAddressByIndex(uint index)
    {
        var newAddressInteger = RangeStart + TUint.CreateTruncating(index % RangeSize);

        return IntToAddress(newAddressInteger);
    }

    private static TUint AddressToInt(IPAddress address)
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<TUint>()];

        address.TryWriteBytes(buffer, out _);

        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        return MemoryMarshal.Cast<byte, TUint>(buffer)[0];
    }

    private static IPAddress IntToAddress(TUint address)
    {
        var buffer = MemoryMarshal.Cast<TUint, byte>(MemoryMarshal.CreateSpan(ref address, 1));

        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        return new IPAddress(buffer);
    }
}
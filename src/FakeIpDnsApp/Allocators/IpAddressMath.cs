using System;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FakeIpDnsApp.Allocators;

/// <summary>
/// Перевод адреса в числовое значение и обратно. Размер числа должен совпадать с длиной адреса:
/// <see cref="uint"/> для IPv4, <see cref="UInt128"/> для IPv6.
/// </summary>
internal static class IpAddressMath
{
    internal static TUint ToInt<TUint>(IPAddress address) where TUint : struct, IBinaryInteger<TUint>
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<TUint>()];

        address.TryWriteBytes(buffer, out _);

        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        return MemoryMarshal.Cast<byte, TUint>(buffer)[0];
    }

    internal static IPAddress ToAddress<TUint>(TUint address) where TUint : struct, IBinaryInteger<TUint>
    {
        var buffer = MemoryMarshal.Cast<TUint, byte>(MemoryMarshal.CreateSpan(ref address, 1));

        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        return new IPAddress(buffer);
    }
}

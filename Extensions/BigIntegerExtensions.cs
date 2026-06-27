using System;
using System.Net;
using System.Numerics;

namespace FakeIpRouting.Extensions;

public static class BigIntegerExtensions
{
    extension(BigInteger bigInteger)
    {
        public IPAddress ToIpAddress(int bufferSize)
        {
            Span<byte> buffer = stackalloc byte[bufferSize];
            
            bigInteger.TryWriteBytes(buffer, out _, isBigEndian: true);

            return new IPAddress(buffer);
        }
    }
}
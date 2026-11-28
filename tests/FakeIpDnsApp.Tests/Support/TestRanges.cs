using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FakeIpDnsApp.Allocators;

namespace FakeIpDnsApp.Tests.Support;

/// <summary>
/// Помощники тестов аллокатора: разбор диапазона строкой, аллокатор по строке, снимок аренды как списка строк.
/// </summary>
public static class TestRanges
{
    /// <summary>Диапазон из строки конфигурации (CIDR или формат A-B).</summary>
    public static IPAddressRange Range(string value)
    {
        return IPAddressRange.Parse(value);
    }

    public static Ipv4Allocator Ipv4(string value)
    {
        return new Ipv4Allocator(IPAddressRange.Parse(value));
    }

    public static Ipv6Allocator Ipv6(string value)
    {
        return new Ipv6Allocator(IPAddressRange.Parse(value));
    }

    /// <summary>Адреса аренды по порядку, строками - чтобы сравнение падения было читаемым.</summary>
    public static string[] Addresses(IpLease<uint> lease)
    {
        return [.. lease.Select(x => x.ToString())];
    }

    /// <summary>Адреса без порядка (сравнение как множества).</summary>
    public static string[] Sorted(IEnumerable<IPAddress> addresses)
    {
        return [.. addresses.Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal)];
    }

    public static string[] Sorted(IpLease<uint> lease)
    {
        return Sorted([.. lease]);
    }

    /// <summary>Числовое значение IPv4-адреса - для проверки смежности адресов аренды.</summary>
    public static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}

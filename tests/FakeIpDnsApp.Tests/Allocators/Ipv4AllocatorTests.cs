using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using FakeIpDnsApp.Allocators;
using FakeIpDnsApp.Tests.Support;
using Xunit;

namespace FakeIpDnsApp.Tests.Allocators;

/// <summary>
/// IPv4-аллокатор: проверка семейства адреса, границы диапазона (беззнаковое сравнение), предельные адреса.
/// </summary>
public sealed class Ipv4AllocatorTests
{
    [Fact]
    public void Ctor_WithIpv6Range_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => TestRanges.Ipv4("2001:db8::/64"));
    }

    [Fact]
    public void Ctor_WithWholeIpv4Space_ThrowsNotSupportedException()
    {
        // /0 = 2^32 адресов
        var exception = Assert.Throws<NotSupportedException>(() => TestRanges.Ipv4("0.0.0.0/0"));

        Assert.Contains("too big", exception.Message);
        Assert.Contains("4294967296", exception.Message);
        Assert.Contains("2147483647", exception.Message); // лимит - int.MaxValue адресов
    }

    [Fact]
    public void Ctor_WithSlash1_ThrowsBecauseRangeIsOneAddressBiggerThanLimit()
    {
        // /1 = 2^31 адресов = int.MaxValue + 1
        var exception = Assert.Throws<NotSupportedException>(() => TestRanges.Ipv4("0.0.0.0/1"));

        Assert.Contains("too big", exception.Message);
        Assert.Contains("2147483648", exception.Message);
    }

    [Fact]
    public void Ctor_WithSlash2_WorksAsLargestIpv4Cidr()
    {
        var allocator = TestRanges.Ipv4("0.0.0.0/2"); // 2^30 адресов

        Assert.Equal(new BigInteger(1073741824), allocator.Range.AddressCount);
        Assert.Equal("0.0.0.0", allocator.Allocate()[0].ToString());
    }

    [Theory]
    [InlineData("0.0.0.1-255.255.255.255", 4294967295)] // 2^32 - 1: begin = 1, end = 2^32 - 1
    [InlineData("100.0.0.1-200.0.0.1", 1677721601)]
    [InlineData("128.0.0.0-128.0.0.1", 2)]
    [InlineData("255.255.255.255-255.255.255.255", 1)]
    public void ParseRange_RangeCrossingHighBitOfAddress_CountsCorrectly(string value, long count)
    {
        // раньше байты адреса читались знаковым BigInteger: адрес со старшим битом 1
        // (>= 128.0.0.0 для IPv4) становился отрицательным и диапазон отвергался как «перевёрнутый»
        Assert.Equal(new BigInteger(count), TestRanges.Range(value).AddressCount);
    }

    [Fact]
    public void Ctor_WithRangeOfExactlyMaxRangeSizeAddresses_Works()
    {
        var allocator = TestRanges.Ipv4("0.0.0.0-127.255.255.254"); // ровно int.MaxValue адресов

        Assert.Equal(new BigInteger(Ipv4Allocator.MaxRangeSize), allocator.Range.AddressCount);
        Assert.Equal("0.0.0.0", allocator.Allocate()[0].ToString());
        Assert.Equal("0.0.0.1", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void Ctor_WithRangeOfMaxRangeSizePlusOneAddresses_IsRejected()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => TestRanges.Ipv4("0.0.0.0-127.255.255.255")); // 2^31 адресов

        Assert.Contains("too big", exception.Message);
        Assert.Contains("2147483648", exception.Message);
    }

    [Theory]
    [InlineData("192.168.0.0/24", "192.168.0.0", true)]
    [InlineData("192.168.0.0/24", "192.168.0.255", true)]
    [InlineData("192.168.0.0/24", "192.168.1.0", false)]
    [InlineData("192.168.0.0/24", "192.169.0.0", false)]
    [InlineData("192.168.5.7/24", "192.168.5.0", true)]
    [InlineData("10.0.0.0/8", "10.255.255.255", true)]
    [InlineData("10.0.0.0/8", "9.255.255.255", false)]
    [InlineData("198.18.0.5/32", "198.18.0.5", true)]
    [InlineData("198.18.0.5/32", "198.18.0.6", false)]
    [InlineData("0.0.0.1-0.0.0.2", "0.0.0.0", false)]
    [InlineData("0.0.0.1-0.0.0.2", "0.0.0.1", true)]
    [InlineData("0.0.0.1-0.0.0.2", "0.0.0.2", true)]
    [InlineData("0.0.0.1-0.0.0.2", "0.0.0.3", false)]
    [InlineData("255.255.255.254-255.255.255.255", "255.255.255.255", true)]
    [InlineData("255.255.255.254-255.255.255.255", "255.255.255.253", false)]
    public void RangeContains_ChecksBoundariesWithoutBorrow(string range, string address, bool expected)
    {
        var allocator = TestRanges.Ipv4(range);

        Assert.Equal(expected, allocator.RangeContains(IPAddress.Parse(address)));
    }

    [Fact]
    public void RangeContains_Ipv6Address_IsFalseEvenIfNumbersWouldMatch()
    {
        // без override по семейству IPv6-адрес прочитался бы как младшие 4 байта и пролез в диапазон
        var allocator = TestRanges.Ipv4("192.0.2.0/30");

        Assert.True(allocator.RangeContains(IPAddress.Parse("192.0.2.1")));
        Assert.False(allocator.RangeContains(IPAddress.Parse("::192.0.2.1")));
        Assert.False(allocator.RangeContains(IPAddress.Parse("fc00::1")));
    }

    [Fact]
    public void Allocate_LastLeaseBeforeMaxAddress_DoesNotOverflow()
    {
        var allocator = TestRanges.Ipv4("255.255.255.254-255.255.255.255");

        Assert.Equal(["255.255.255.254", "255.255.255.255"], TestRanges.Addresses(allocator.Allocate(2)));
        Assert.Equal("255.255.255.254", allocator.Allocate(1)[0].ToString());
    }

    [Fact]
    public void Allocate_CycleOverRange_IssuesEveryAddressExactlyOnce()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");
        var issued = new List<IPAddress>();

        for (var i = 0; i < 256; i++)
        {
            issued.Add(allocator.Allocate()[0]);
        }

        Assert.Equal(256, issued.Distinct().Count());
        Assert.Contains(IPAddress.Parse("192.0.2.0"), issued);
        Assert.Contains(IPAddress.Parse("192.0.2.255"), issued);
    }
}

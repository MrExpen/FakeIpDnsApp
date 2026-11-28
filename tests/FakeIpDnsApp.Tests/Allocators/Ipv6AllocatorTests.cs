using System;
using System.Net;
using System.Numerics;
using FakeIpDnsApp.Tests.Support;
using Xunit;

namespace FakeIpDnsApp.Tests.Allocators;

/// <summary>
/// IPv6-аллокатор (TUint = UInt128): предельные префиксы, границы диапазона, адрес максимума.
/// </summary>
public sealed class Ipv6AllocatorTests
{
    [Fact]
    public void Ctor_WithIpv4Range_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => TestRanges.Ipv6("198.18.0.0/15"));
    }

    [Fact]
    public void Ctor_WithSlash97_ThrowsBecauseRangeIsOneAddressBiggerThanLimit()
    {
        // /97 = 2^31 адресов, а лимит аллокатора - int.MaxValue = 2^31 - 1
        var exception = Assert.Throws<NotSupportedException>(() => TestRanges.Ipv6("2001:db8::/97"));

        Assert.Contains("too big", exception.Message);
        Assert.Contains("2147483648", exception.Message);
    }

    [Fact]
    public void Ctor_WithSlash96_ThrowsBecauseTooBig()
    {
        // /96 = 2^32 адресов
        var exception = Assert.Throws<NotSupportedException>(() => TestRanges.Ipv6("2001:db8::/96"));

        Assert.Contains("too big", exception.Message);
        Assert.Contains("/98", exception.Message);
    }

    [Fact]
    public void Ctor_WithSlash98_WorksAsLargestIpv6Cidr()
    {
        var allocator = TestRanges.Ipv6("2001:db8::/98"); // 2^30 адресов

        Assert.Equal(new BigInteger(1073741824), allocator.Range.AddressCount);
        Assert.Equal("2001:db8::", allocator.Allocate()[0].ToString());
    }

    [Theory]
    [InlineData("fc00::/110", "fc00::", true)]
    [InlineData("fc00::/110", "fc00::3:ffff", true)]   // последний адрес /110 (2^18 - 1)
    [InlineData("fc00::/110", "fc00::4:0", false)]
    [InlineData("fc00::/110", "fc00::2:ffff", true)]
    [InlineData("fc00::/110", "fd00::1", false)]
    [InlineData("fc00::/110", "::1", false)]
    [InlineData("fc00::/128", "fc00::", true)]
    [InlineData("fc00::/128", "fc00::1", false)]
    [InlineData("2001:db8::/98", "2001:db8::3fff:ffff", true)]
    [InlineData("2001:db8::/98", "2001:db8::4000:0", false)]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff/128", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff/128", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffe", false)]
    public void RangeContains_ChecksBoundaries(string range, string address, bool expected)
    {
        var allocator = TestRanges.Ipv6(range);

        Assert.Equal(expected, allocator.RangeContains(IPAddress.Parse(address)));
    }

    [Fact]
    public void RangeContains_Ipv4Address_IsFalseEvenIfNumbersWouldMatch()
    {
        var allocator = TestRanges.Ipv6("fc00::/110");

        Assert.False(allocator.RangeContains(IPAddress.Parse("192.0.2.1")));
        Assert.False(allocator.RangeContains(IPAddress.Parse("0.0.0.0")));
    }

    [Fact]
    public void Allocate_WalksUlaRangeAndWraps()
    {
        var allocator = TestRanges.Ipv6("fc00::/126"); // 4 адреса

        Assert.Equal("fc00::", allocator.Allocate()[0].ToString());
        Assert.Equal("fc00::1", allocator.Allocate()[0].ToString());
        Assert.Equal("fc00::2", allocator.Allocate()[0].ToString());
        Assert.Equal("fc00::3", allocator.Allocate()[0].ToString());
        Assert.Equal("fc00::", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void Allocate_RangeEndingAtMaxAddress_LeaseStaysInRange()
    {
        var allocator = TestRanges.Ipv6("ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffd-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff");

        var lease = allocator.Allocate(3);

        Assert.Equal("ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffd", lease[0].ToString());
        Assert.Equal("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", lease[2].ToString());
        Assert.True(allocator.RangeContains(lease[2]));
    }

    [Fact]
    public void Allocate_MultiAddressLease_SplitsRangeInOrder()
    {
        var allocator = TestRanges.Ipv6("fc00::10-fc00::17"); // 8 адресов

        var first = allocator.Allocate(3);
        var second = allocator.Allocate(3);

        Assert.Equal("fc00::10", first[0].ToString());
        Assert.Equal("fc00::12", first[2].ToString());
        Assert.Equal("fc00::13", second[0].ToString());
        Assert.Equal("fc00::15", second[2].ToString());

        // до конца диапазона осталось 2 адреса из 3: арендуется остаток, пересечения с second нет
        var third = allocator.Allocate(3);
        Assert.Equal(2U, third.Count);
        Assert.Equal("fc00::16", third[0].ToString());
        Assert.Equal("fc00::17", third[1].ToString());
        Assert.Equal("fc00::15", second[2].ToString());

        Assert.Equal("fc00::10", allocator.Allocate(3)[0].ToString());
    }

    [Fact]
    public void TryReturn_OfLastLease_RollsRingBack()
    {
        var allocator = TestRanges.Ipv6("fc00::/122"); // 64 адреса

        var lease = allocator.Allocate(2);

        Assert.Equal("fc00::", lease[0].ToString());
        Assert.True(allocator.TryReturn(lease));
        Assert.Equal("fc00::", allocator.Allocate(2)[0].ToString());
    }
}

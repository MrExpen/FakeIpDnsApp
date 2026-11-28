using System;
using System.Net;
using System.Numerics;
using Xunit;

namespace FakeIpDnsApp.Tests;

/// <summary>
/// Разбор диапазонов из dnsApp.config: CIDR и формат A-B, нормализация старшего адреса, равенство по
/// (начало, количество) - на этом равенстве держится переиспользование аллокатора при перезагрузке конфига.
/// </summary>
public sealed class IPAddressRangeTests
{
    [Theory]
    [InlineData("198.18.0.0/15", "198.18.0.0", 131072)]
    [InlineData("10.0.0.0/9", "10.0.0.0", 8388608)]
    [InlineData("172.16.0.0/12", "172.16.0.0", 1048576)]
    [InlineData("192.168.5.7/24", "192.168.5.0", 256)]
    [InlineData("10.20.30.40/32", "10.20.30.40", 1)]
    [InlineData("10.0.0.0/8", "10.0.0.0", 16777216)]
    [InlineData("10.0.0.1/+24", "10.0.0.0", 256)] // знаковый префикс int.Parse принимает
    public void ParseCidr_NormalizesHostBitsAndCountsHosts(string value, string begin, long count)
    {
        var range = IPAddressRange.Parse(value);

        Assert.Equal(begin, range.Begin.ToString());
        Assert.Equal(new BigInteger(count), range.AddressCount);
    }

    [Theory]
    [InlineData("0.0.0.0/0", "0.0.0.0", 4294967296)]
    [InlineData("0.0.0.0/1", "0.0.0.0", 2147483648)]
    [InlineData("255.255.255.255/0", "0.0.0.0", 4294967296)]
    [InlineData("255.255.255.254/31", "255.255.255.254", 2)]
    [InlineData("255.255.255.255/31", "255.255.255.254", 2)]
    public void ParseCidr_EditorialEdgePrefixes(string value, string begin, long count)
    {
        var range = IPAddressRange.Parse(value);

        Assert.Equal(begin, range.Begin.ToString());
        Assert.Equal(new BigInteger(count), range.AddressCount);
    }

    [Theory]
    [InlineData("2001:db8::/64", "2001:db8::")]
    [InlineData("2001:db8::1/64", "2001:db8::")]
    [InlineData("2001:db8::/96", "2001:db8::")]
    [InlineData("2001:db8::/97", "2001:db8::")]
    [InlineData("2001:db8::/128", "2001:db8::")]
    [InlineData("fc00::/110", "fc00::")]
    [InlineData("::/0", "::")]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff/128", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    public void ParseCidr_Ipv6KeepsNormalizedBeginAndFullHostCount(string value, string begin)
    {
        var range = IPAddressRange.Parse(value);

        Assert.Equal(begin, range.Begin.ToString());
    }

    [Fact]
    public void ParseCidr_Ipv6HostCountMatchesPrefix()
    {
        Assert.Equal(new BigInteger(4294967296), IPAddressRange.Parse("2001:db8::/96").AddressCount);
        Assert.Equal(new BigInteger(2147483648), IPAddressRange.Parse("2001:db8::/97").AddressCount);
        Assert.Equal(BigInteger.One << 128, IPAddressRange.Parse("::/0").AddressCount);
        Assert.Equal(BigInteger.One, IPAddressRange.Parse("::1/128").AddressCount);
    }

    [Theory]
    [InlineData("10.0.0.1/33")]
    [InlineData("10.0.0.1/256")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.1/")]
    [InlineData("10.0.0.1/abc")]
    public void ParseCidr_InvalidPrefix_ThrowsFormatException(string value)
    {
        Assert.Throws<FormatException>(() => IPAddressRange.Parse(value));
    }

    [Theory]
    [InlineData("10.0.0.1/-1")]
    [InlineData("2001:db8::/-1")]
    [InlineData("10.0.0.1/-33")]
    [InlineData("2001:db8::/-129")]
    public void ParseCidr_NegativePrefix_ThrowsFormatException(string value)
    {
        // раньше отрицательный префикс проходил проверку hostLength > addressLength
        // и падал IndexOutOfRangeException при маскировании байтов
        Assert.Throws<FormatException>(() => IPAddressRange.Parse(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.1")]
    [InlineData("198.18.0.0 15")]
    [InlineData("2001:db8::")]
    [InlineData("-")]
    [InlineData("/24")]
    [InlineData("10.0.0.1:24")]
    [InlineData("10.0.0.1 //24")]
    [InlineData(" 10.0.0.1/24")]
    [InlineData("2001:db8::-")]
    public void Parse_WithoutUsableSeparator_ThrowsFormatException(string value)
    {
        Assert.Throws<FormatException>(() => IPAddressRange.Parse(value));
    }

    [Theory]
    [InlineData("10.0.0.1-10.0.0.1", 1)]
    [InlineData("10.0.0.0-10.0.0.4", 5)]
    [InlineData("198.18.0.0-198.19.255.255", 131072)]
    [InlineData("0.0.0.0-0.0.0.1", 2)]
    [InlineData("255.255.255.255-255.255.255.255", 1)]
    public void ParseRange_CountsBothEndsInclusive(string value, long count)
    {
        var range = IPAddressRange.Parse(value);

        Assert.Equal(new BigInteger(count), range.AddressCount);
    }

    [Fact]
    public void ParseRange_Ipv6()
    {
        var range = IPAddressRange.Parse("2001:db8::-2001:db8::ffff:ffff");

        Assert.Equal("2001:db8::", range.Begin.ToString());
        Assert.Equal(new BigInteger(4294967296), range.AddressCount);
    }

    [Theory]
    [InlineData("10.0.0.5-10.0.0.1")]
    [InlineData("2001:db8::1-2001:db8::")]
    public void ParseRange_Reversed_ThrowsInvalidOperationException(string value)
    {
        Assert.Throws<InvalidOperationException>(() => IPAddressRange.Parse(value));
    }

    [Fact]
    public void ParseRange_EndAddressWithHighBit_CountsCorrectly()
    {
        // 255.255.255.255 и 255.255.255.254 читаются беззнаково, а не как -1 и -2
        var range = IPAddressRange.Parse("255.255.255.254-255.255.255.255");

        Assert.Equal(new BigInteger(2), range.AddressCount);
        Assert.Equal("255.255.255.254", range.Begin.ToString());
    }

    [Fact]
    public void ParseRange_Ipv6CrossingHighBit_CountsCorrectly()
    {
        var range = IPAddressRange.Parse("::1-ffff::1");

        // ffff::1 = 2^128 - 2^112 + 1, начало ::1 -> количество = end - begin + 1
        Assert.Equal((BigInteger.One << 128) - (BigInteger.One << 112) + 1, range.AddressCount);
        Assert.Equal("::1", range.Begin.ToString());

        var whole = IPAddressRange.Parse("::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff");

        Assert.Equal(BigInteger.One << 128, whole.AddressCount);
    }

    [Theory]
    [InlineData("10.0.0.1-::5")]
    [InlineData("::1-10.0.0.5")]
    public void ParseRange_MixedAddressFamilies_ThrowsInvalidOperationException(string value)
    {
        Assert.Throws<InvalidOperationException>(() => IPAddressRange.Parse(value));
    }

    [Theory]
    [InlineData("198.18.0.0/15", "198.18.0.0-198.19.255.255", true)]
    [InlineData("198.18.0.0/15", "198.18.0.0-198.19.255.254", false)]
    [InlineData("198.18.0.0/15", "198.18.0.1-198.19.255.255", false)]
    [InlineData("10.0.0.0/8", "10.0.0.0-10.255.255.255", true)]
    [InlineData("2001:db8::/96", "2001:db8::-2001:db8::ffff:ffff", true)]
    public void Equals_ComparesBeginAndCount_NotTheWrittenText(string left, string right, bool expected)
    {
        Assert.Equal(expected, IPAddressRange.Parse(left).Equals(IPAddressRange.Parse(right)));
    }

    [Fact]
    public void GetHashCode_MatchesForEqualRangesWrittenDifferently()
    {
        Assert.Equal(
            IPAddressRange.Parse("198.18.0.0/15").GetHashCode(),
            IPAddressRange.Parse("198.18.0.0-198.19.255.255").GetHashCode());
    }

    [Fact]
    public void Equals_NullOrOtherType_IsFalse()
    {
        var range = IPAddressRange.Parse("198.18.0.0/15");

        Assert.False(range.Equals(null));
        Assert.False(range.Equals("198.18.0.0/15"));
        Assert.False(range.Equals(IPAddress.Parse("198.18.0.0")));
        Assert.True(range.Equals(range));
    }

    [Fact]
    public void Parse_AcceptsSpanSlices_OfLongerText()
    {
        ReadOnlySpan<char> text = "range=198.18.0.0/15;end";

        var range = IPAddressRange.Parse(text.Slice(6, 13));

        Assert.Equal("198.18.0.0", range.Begin.ToString());
        Assert.Equal(new BigInteger(131072), range.AddressCount);
    }
}

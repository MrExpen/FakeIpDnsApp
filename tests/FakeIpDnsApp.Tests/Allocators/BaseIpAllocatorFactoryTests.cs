using System;
using FakeIpDnsApp.Allocators;
using FakeIpDnsApp.Tests.Support;
using Xunit;

namespace FakeIpDnsApp.Tests.Allocators;

/// <summary>
/// Фабрика аллокаторов: ключ - сам диапазон (Equals/GetHashCode IPAddressRange), а не строка из конфига.
/// Именно за счёт этого при перечитывании dnsApp.config кольцо адресов не сбрасывается.
/// </summary>
public sealed class BaseIpAllocatorFactoryTests
{
    [Fact]
    public void Create_NullRange_ReturnsNull()
    {
        Assert.Null(new Ipv4AllocatorFactory().Create((string?)null));
        Assert.Null(new Ipv6AllocatorFactory().Create((string?)null));
    }
    [Fact]
    public void Create_InvalidRangeString_ThrowsFormatException()
    {
        var factory = new Ipv4AllocatorFactory();

        Assert.Throws<FormatException>(() => factory.Create("198.18.0.0"));
        Assert.Throws<FormatException>(() => factory.Create("198.18.0.0/15x"));
    }

    [Fact]
    public void Create_EqualRangesWrittenDifferently_ReturnSameAllocator()
    {
        var factory = new Ipv4AllocatorFactory();

        var byCidr = factory.Create("198.18.0.0/15");
        var byBounds = factory.Create("198.18.0.0-198.19.255.255");

        Assert.NotNull(byCidr);
        Assert.Same(byCidr, byBounds);
    }

    [Fact]
    public void Create_DifferentRanges_ReturnDifferentAllocators()
    {
        var factory = new Ipv4AllocatorFactory();

        Assert.NotSame(factory.Create("198.18.0.0/15"), factory.Create("198.19.0.0/16"));
        Assert.NotSame(factory.Create("198.18.0.0/15"), factory.Create("198.18.0.0/16"));
    }

    [Fact]
    public void Create_NormalizedCidrSharesAllocatorWithNormalizedBegin()
    {
        var factory = new Ipv4AllocatorFactory();

        // 198.18.5.7/24 нормализуется в 198.18.5.0/24 - это тот же ключ
        Assert.Same(factory.Create("198.18.5.7/24"), factory.Create("198.18.5.0/24"));
    }

    [Fact]
    public void TryAdd_Null_ReturnsFalse()
    {
        Assert.False(new Ipv4AllocatorFactory().TryAdd(null));
    }

    [Fact]
    public void TryAdd_ExistingRange_KeepsFirstAllocatorAndItsRing()
    {
        var factory = new Ipv4AllocatorFactory();
        var original = factory.Create("198.18.0.0/24")!;

        Assert.Equal("198.18.0.0", original.Allocate()[0].ToString());

        var fresh = new Ipv4AllocatorFactory().Create("198.18.0.0/24")!;
        Assert.False(factory.TryAdd(fresh));

        // второй аллокатор не заменил первый: кольцо продолжается, а не начинается заново
        Assert.Same(original, factory.Create("198.18.0.0/24"));
        Assert.Equal("198.18.0.1", original.Allocate()[0].ToString());
    }

    [Fact]
    public void AddRange_RegistersPreviousAllocators_LikeConfigReload()
    {
        var first = new Ipv4AllocatorFactory();
        var old = first.Create("198.18.0.0/24")!;

        old.Allocate(2); // кольцо на 198.18.0.2

        var second = new Ipv4AllocatorFactory();
        second.AddRange([old]);

        var reused = second.Create("198.18.0.0/24");

        Assert.Same(old, reused);
        Assert.Equal("198.18.0.2", reused!.Allocate()[0].ToString());
    }

    [Fact]
    public void TryAdd_NullableAllocatorFromPreviousConfig_RegistersItAndKeepsRing()
    {
        var first = new Ipv4AllocatorFactory();
        Ipv4Allocator? old = first.Create("198.18.0.0/24");

        old!.Allocate(2);

        var next = new Ipv4AllocatorFactory();
        Assert.True(next.TryAdd(old));

        var reused = next.Create("198.18.0.0/24");

        Assert.Same(old, reused);
        Assert.Equal("198.18.0.2", reused!.Allocate()[0].ToString());
    }

    [Fact]
    public void TryAdd_RangeOfDifferentSize_IsRegisteredSeparately()
    {
        var factory = new Ipv4AllocatorFactory();

        Assert.True(factory.TryAdd(new Ipv4Allocator(TestRanges.Range("198.18.0.0/24"))));
        Assert.True(factory.TryAdd(new Ipv4Allocator(TestRanges.Range("198.18.0.0/25"))));

        Assert.Same(factory.Create("198.18.0.0/24"), factory.Create("198.18.0.0/24"));
        Assert.NotSame(factory.Create("198.18.0.0/24"), factory.Create("198.18.0.0/25"));
    }

    [Fact]
    public void Ipv6Factory_CreateSlash96Range_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => new Ipv6AllocatorFactory().Create("2001:db8::/96"));
    }

    [Fact]
    public void Ipv4Factory_CreateIpv6Range_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => new Ipv4AllocatorFactory().Create("fc00::/110"));
    }

    [Fact]
    public void Ipv6Factory_SharedRangeKeyAcrossFormats()
    {
        var factory = new Ipv6AllocatorFactory();

        Assert.Same(factory.Create("fc00::/122"), factory.Create("fc00::-fc00::3f"));
    }
}

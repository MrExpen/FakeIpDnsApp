using FakeIpDnsApp.Allocators;
using FakeIpDnsApp.Tests.Support;
using Xunit;

namespace FakeIpDnsApp.Tests.Allocators;

/// <summary>
/// Возврат аренды в пул: best-effort откат кольца, отказ когда кольцо уже ушло, отказ для чужой аренды.
/// </summary>
public sealed class BaseIpAllocatorTryReturnTests
{
    [Fact]
    public void TryReturn_OfLastLease_RollsRingBackAndAddressesAreIssuedAgain()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");

        var lease = allocator.Allocate(3); // 192.0.2.0 .. 192.0.2.2

        Assert.True(allocator.TryReturn(lease));
        Assert.Equal(TestRanges.Addresses(lease), TestRanges.Addresses(allocator.Allocate(3)));
    }

    [Fact]
    public void TryReturn_OfLastLease_MovesRingBackToPreviousLeaseEnd()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");

        var first = allocator.Allocate(2);  // 0,1
        var second = allocator.Allocate(2); // 2,3

        Assert.True(allocator.TryReturn(second));
        // кольцо откатилось на конец первой аренды: вторая выдаётся заново
        Assert.Equal(["192.0.2.2", "192.0.2.3"], TestRanges.Addresses(allocator.Allocate(2)));
        Assert.True(allocator.RangeContains(first[1]));
    }

    [Fact]
    public void TryReturn_OlderLease_FailsBecauseRingAlreadyMoved()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");

        var first = allocator.Allocate(1);
        var second = allocator.Allocate(1);

        Assert.False(allocator.TryReturn(first));
        Assert.True(allocator.TryReturn(second));
    }

    [Fact]
    public void TryReturn_Twice_FailsTheSecondTimeForPartialLease()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");

        var lease = allocator.Allocate(2);

        Assert.True(allocator.TryReturn(lease));
        Assert.False(allocator.TryReturn(lease));
    }

    [Fact]
    public void TryReturn_WholeRangeLease_IsIdempotentAndKeepsRingAtStart()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30"); // 4 адреса

        var lease = allocator.Allocate(4);

        Assert.True(allocator.TryReturn(lease));
        Assert.True(allocator.TryReturn(lease));
        // конец такого интервала совпадает с началом кольца, поэтому откат ничего не ломает
        Assert.Equal(["192.0.2.0", "192.0.2.1", "192.0.2.2", "192.0.2.3"], TestRanges.Addresses(allocator.Allocate(4)));
    }

    [Fact]
    public void TryReturn_EmptyLease_ReturnsFalse()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");

        Assert.False(allocator.TryReturn(allocator.Allocate(0)));
        Assert.False(allocator.TryReturn(IpLease<uint>.Empty));
        Assert.Equal("192.0.2.0", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void TryReturn_LeaseOfAnotherAllocator_ReturnsFalse()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");
        var foreign = TestRanges.Ipv4("198.18.0.0/24");

        Assert.False(allocator.TryReturn(foreign.Allocate(2)));
        // кольцо целевого аллокатора не двинулось
        Assert.Equal("192.0.2.0", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void TryReturn_LeaseOfAnotherAddressFamily_IsNotAccepted()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");
        var ipv6 = TestRanges.Ipv6("fc00::/122");

        Assert.Equal("fc00::", ipv6.Allocate(1)[0].ToString());
        // адрес из IPv6-диапазона в IPv4-кольце не возвращается: проверка семейства в RangeContains
        Assert.False(allocator.TryReturn(new IpLease<uint>(0, 1)));
        Assert.Equal("192.0.2.0", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void TryReturn_CountBiggerThanRange_ReturnsFalse()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30"); // 4 адреса

        Assert.False(allocator.TryReturn(new IpLease<uint>(0, 5)));
    }

    [Fact]
    public void TryReturn_TailLeaseAtRangeEnd_IsReturnedAndRingRestartsAtItsStart()
    {
        var allocator = TestRanges.Ipv4("10.0.0.0-10.0.0.4"); // 5 адресов

        allocator.Allocate(2); // 0,1
        allocator.Allocate(2); // 2,3
        var tail = allocator.Allocate(2); // остался 1 адрес: 10.0.0.4, кольцо встало на 0

        Assert.Equal(["10.0.0.4"], TestRanges.Addresses(tail));
        Assert.True(allocator.TryReturn(tail));
        // кольцо откатилось на 4: та же аренда выдаётся заново, затем снова с начала диапазона
        Assert.Equal(["10.0.0.4"], TestRanges.Addresses(allocator.Allocate(2)));
        Assert.Equal(["10.0.0.0", "10.0.0.1"], TestRanges.Addresses(allocator.Allocate(2)));
    }

    [Fact]
    public void TryReturn_FailsOnceRingMovedPastEndOfReturnedLease()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");

        var lease = allocator.Allocate(2);
        Assert.True(allocator.TryReturn(lease));

        // адреса арендуем заново, затем кольцо уходит дальше - повторный возврат невозможен
        var reissued = allocator.Allocate(2);
        Assert.Equal(TestRanges.Addresses(lease), TestRanges.Addresses(reissued));
        Assert.Equal("192.0.2.2", allocator.Allocate(1)[0].ToString());
        Assert.False(allocator.TryReturn(lease));
    }

    [Fact]
    public void TryReturn_LeaseBelowRangeStart_ReturnsFalse()
    {
        // диапазон 0.0.0.1-0.0.0.2: адрес 0.0.0.0 лежит ниже начала
        var allocator = TestRanges.Ipv4("0.0.0.1-0.0.0.2");

        var lease = allocator.Allocate(1);
        Assert.Equal("0.0.0.1", lease[0].ToString());

        Assert.False(allocator.TryReturn(new IpLease<uint>(0, 1)));
        Assert.True(allocator.TryReturn(lease));
        Assert.Equal("0.0.0.1", allocator.Allocate(1)[0].ToString());
    }
}

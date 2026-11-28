using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using FakeIpDnsApp.Tests.Support;
using Xunit;

namespace FakeIpDnsApp.Tests.Allocators;

/// <summary>
/// Кольцевая выдача адресов: порядок, переход через конец диапазона, обрезка слишком длинной аренды,
/// пустая аренда, минимальный диапазон.
/// </summary>
public sealed class BaseIpAllocatorTests
{
    [Fact]
    public void Allocate_SingleAddresses_WalkFromRangeStart()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30"); // 4 адреса

        Assert.Equal("192.0.2.0", allocator.Allocate()[0].ToString());
        Assert.Equal("192.0.2.1", allocator.Allocate()[0].ToString());
        Assert.Equal("192.0.2.2", allocator.Allocate()[0].ToString());
        Assert.Equal("192.0.2.3", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void Allocate_AfterLastAddress_WrapsToRangeStart()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30");

        for (var i = 0; i < 4; i++)
        {
            allocator.Allocate();
        }

        Assert.Equal("192.0.2.0", allocator.Allocate()[0].ToString());
        Assert.Equal("192.0.2.1", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void Allocate_MultiAddressLease_SplitsRangeInOrder()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/29"); // 8 адресов

        Assert.Equal(["192.0.2.0", "192.0.2.1", "192.0.2.2"], TestRanges.Addresses(allocator.Allocate(3)));
        Assert.Equal(["192.0.2.3", "192.0.2.4", "192.0.2.5"], TestRanges.Addresses(allocator.Allocate(3)));
        // до конца диапазона осталось 2 адреса из 3: арендуется остаток, повторных адресов нет
        var tail = allocator.Allocate(3);

        Assert.Equal(2U, tail.Count);
        Assert.Equal(["192.0.2.6", "192.0.2.7"], TestRanges.Addresses(tail));
        Assert.Equal("192.0.2.0", allocator.Allocate(3)[0].ToString());
    }

    [Fact]
    public void Allocate_ZeroCount_ReturnsEmptyLeaseAndDoesNotMoveRing()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30");

        var empty = allocator.Allocate(0);

        Assert.Equal(0U, empty.Count);
        Assert.Empty(empty);
        // кольцо не сдвинулось: следующая аренда начинается с того же места
        Assert.Equal("192.0.2.0", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void Allocate_CountBiggerThanRange_ClampedToWholeRange()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30");

        var lease = allocator.Allocate(10);

        Assert.Equal(4U, lease.Count);
        Assert.Equal(["192.0.2.0", "192.0.2.1", "192.0.2.2", "192.0.2.3"], TestRanges.Addresses(lease));
        // кольцо встало в начало: весь диапазон выдаётся заново, а не по одному адресу
        Assert.Equal(["192.0.2.0", "192.0.2.1", "192.0.2.2", "192.0.2.3"], TestRanges.Addresses(allocator.Allocate(10)));
    }

    [Fact]
    public void Allocate_CountEqualRangeSize_ReturnsWholeRangeOnce()
    {
        var allocator = TestRanges.Ipv4("198.18.0.0/24"); // 256 адресов

        var lease = allocator.Allocate(256);

        Assert.Equal(256U, lease.Count);
        Assert.Equal("198.18.0.0", lease[0].ToString());
        Assert.Equal("198.18.0.255", lease[255].ToString());
        Assert.Equal(256, lease.Distinct().Count());
        Assert.True(allocator.RangeContains(lease[255]));
    }

    [Fact]
    public void Allocate_RangeOfSingleAddress_IssuesSameAddressEveryTime()
    {
        var allocator = TestRanges.Ipv4("198.18.0.5/32");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("198.18.0.5", allocator.Allocate()[0].ToString());
        }

        // TryReturn для аренды длиной в весь диапазон - always true и без сдвига кольца
        Assert.True(allocator.TryReturn(allocator.Allocate(1)));
        Assert.Equal("198.18.0.5", allocator.Allocate()[0].ToString());
    }

    [Fact]
    public void Allocate_RangeOfSingleAddress_CountBiggerThanRange_IsClamped()
    {
        var allocator = TestRanges.Ipv4("198.18.0.5/32");

        Assert.Equal(1U, allocator.Allocate(100).Count);
    }

    [Fact]
    public void Allocate_StartsAtNormalizedCidrBegin_NotAtWrittenAddress()
    {
        // 192.168.5.7/24 нормализуется в 192.168.5.0
        var allocator = TestRanges.Ipv4("192.168.5.7/24");

        Assert.Equal("192.168.5.0", allocator.Range.Begin.ToString());
        Assert.Equal(new BigInteger(256), allocator.Range.AddressCount);
        Assert.Equal("192.168.5.0", allocator.Allocate()[0].ToString());
        Assert.True(allocator.RangeContains(IPAddress.Parse("192.168.5.255")));
    }

    [Fact]
    public void Allocate_LeaseAddressesAreAlwaysContiguousAndInsideRange()
    {
        var allocator = TestRanges.Ipv4("198.18.0.0-198.18.0.14"); // 15 адресов, не делится на 3

        for (var i = 0; i < 100; i++)
        {
            var lease = allocator.Allocate(3);

            Assert.Equal(3U, lease.Count);
            Assert.Equal(3, lease.Distinct().Count());

            // адреса аренды идут подряд, сама аренда не оборачивается внутри диапазона
            for (var index = 1; index < lease.Count; index++)
            {
                Assert.Equal(TestRanges.ToUInt32(lease[index - 1]) + 1, TestRanges.ToUInt32(lease[index]));
            }

            foreach (var address in lease)
            {
                Assert.True(allocator.RangeContains(address), $"{address} вне диапазона {allocator.Range.Begin}");
            }
        }
    }

    [Fact]
    public void Allocate_NonDivisibleRange_KeepsEveryAddressReachable()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0-192.0.2.9"); // 10 адресов
        var seen = new HashSet<string>();

        for (var i = 0; i < 40; i++)
        {
            foreach (var address in allocator.Allocate(3))
            {
                seen.Add(address.ToString());
            }
        }

        Assert.Equal(10, seen.Count);
    }

    // --- пересечения аренды с предыдущей больше нет: берётся ровно остаток хвоста ---

    [Fact]
    public void Allocate_LeaseNotFittingBeforeRangeEnd_TakesRemainingTailAndStaysDisjoint()
    {
        var allocator = TestRanges.Ipv4("10.0.0.1-10.0.0.7"); // 7 адресов

        var first = allocator.Allocate(3);  // 10.0.0.1 .. 10.0.0.3
        var second = allocator.Allocate(3); // 10.0.0.4 .. 10.0.0.6
        var third = allocator.Allocate(3);  // остался 1 адрес: 10.0.0.7

        Assert.Equal(["10.0.0.1", "10.0.0.2", "10.0.0.3"], TestRanges.Addresses(first));
        Assert.Equal(["10.0.0.4", "10.0.0.5", "10.0.0.6"], TestRanges.Addresses(second));
        Assert.Equal(1U, third.Count);
        Assert.Equal(["10.0.0.7"], TestRanges.Addresses(third));

        Assert.Empty(first.Intersect(second));
        Assert.Empty(second.Intersect(third));

        // виток закрыт, следующая аренда снова с начала диапазона
        Assert.Equal("10.0.0.1", allocator.Allocate(1)[0].ToString());
    }

    [Theory]
    [InlineData("10.0.0.0-10.0.0.4", 2)]
    [InlineData("192.0.2.0/29", 3)]
    [InlineData("10.0.0.0-10.0.0.9", 4)]
    [InlineData("198.18.0.0-198.18.0.16", 5)]
    [InlineData("192.0.2.0/24", 7)]
    [InlineData("198.18.0.5/32", 1)]
    [InlineData("10.0.0.0-10.0.0.9", 25)] // просят больше, чем в диапазоне
    public void Allocate_IssuedAddressesFollowRingOrder_WithoutSkipsOrRepeats(string range, uint count)
    {
        var allocator = TestRanges.Ipv4(range);
        var parsed = TestRanges.Range(range);
        var size = (int)parsed.AddressCount;
        var firstAddress = TestRanges.ToUInt32(parsed.Begin);

        var issued = new List<uint>();

        for (var i = 0; i < 300; i++)
        {
            var lease = allocator.Allocate(count);

            Assert.True(lease.Count > 0);
            Assert.True(lease.Count <= count);
            Assert.True(lease.Count <= size);

            issued.AddRange(lease.Select(TestRanges.ToUInt32));
        }

        // адресная последовательность - это ровно обход кольца: 0, 1, 2, ... по модулю размера диапазона
        for (var i = 0; i < issued.Count; i++)
        {
            Assert.Equal(firstAddress + (uint)(i % size), issued[i]);
        }
    }

    [Fact]
    public void Allocate_MixedLeaseSizes_StillCoverRangeEvenly()
    {
        var allocator = TestRanges.Ipv4("10.0.0.0-10.0.0.9"); // 10 адресов
        var issued = new List<IPAddress>();

        // разные домены одного диапазона просят разное количество адресов
        uint[] sizes = [3, 7, 2, 9, 4, 4, 1, 6];

        for (var round = 0; round < 10; round++)
        {
            foreach (var size in sizes)
            {
                issued.AddRange(allocator.Allocate(size));
            }
        }

        var counts = issued.GroupBy(x => x).Select(g => g.Count()).ToArray();

        Assert.Equal(10, counts.Length); // весь диапазон покрыт
        Assert.True(counts.Max() - counts.Min() <= 1, $"выдача неровная: {string.Join(", ", counts)}");
    }

    [Fact]
    public void Allocate_RangeSizeDivisibleByCount_MakesDisjointCycles()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30"); // 4 адреса

        Assert.Equal(["192.0.2.0", "192.0.2.1"], TestRanges.Addresses(allocator.Allocate(2)));
        Assert.Equal(["192.0.2.2", "192.0.2.3"], TestRanges.Addresses(allocator.Allocate(2)));
        Assert.Equal(["192.0.2.0", "192.0.2.1"], TestRanges.Addresses(allocator.Allocate(2)));
    }

    [Fact]
    public void Range_ExposesParsedRange_AsFactoryKey()
    {
        var allocator = TestRanges.Ipv4("198.18.0.0/15");

        Assert.Equal(TestRanges.Range("198.18.0.0/15"), allocator.Range);
        Assert.Equal(TestRanges.Range("198.18.0.0-198.19.255.255"), allocator.Range);
    }

    [Fact]
    public void Allocate_CountBiggerThanRemainingTail_TakesOnlyTailWithoutOverlap()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/29"); // 8 адресов

        Assert.Equal(["192.0.2.0", "192.0.2.1", "192.0.2.2"], TestRanges.Addresses(allocator.Allocate(3)));

        // просят 10 адресов при 5 оставшихся: берётся остаток, уже выданные 192.0.2.0-192.0.2.2 не повторяются
        var tail = allocator.Allocate(10);

        Assert.Equal(5U, tail.Count);
        Assert.Equal(["192.0.2.3", "192.0.2.4", "192.0.2.5", "192.0.2.6", "192.0.2.7"], TestRanges.Addresses(tail));

        Assert.Equal("192.0.2.0", allocator.Allocate(1)[0].ToString());
    }

    [Fact]
    public void Allocate_CycleWithCountNotDividingRangeSize_CoversRangeOncePerCycle()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/29"); // 8 адресов
        var issued = new List<IPAddress>();
        var sizes = new List<uint>();

        for (var i = 0; i < 3; i++) // три аренды по 3 адреса - один полный виток кольца
        {
            var lease = allocator.Allocate(3);

            sizes.Add(lease.Count);
            issued.AddRange(lease);
        }

        Assert.Equal([3U, 3U, 2U], sizes); // хвост короче, зато без повторов
        Assert.Equal(8, issued.Count);
        Assert.Equal(8, issued.Distinct().Count());
    }

    [Fact]
    public void Allocate_FourAddressLeasesOverTenAddressRange_TailDoesNotOverlapPreviousLease()
    {
        var allocator = TestRanges.Ipv4("10.0.0.0-10.0.0.9"); // 10 адресов

        var first = allocator.Allocate(4);  // 10.0.0.0 .. 10.0.0.3
        var second = allocator.Allocate(4); // 10.0.0.4 .. 10.0.0.7
        var third = allocator.Allocate(4);  // осталось 2 адреса: 10.0.0.8 .. 10.0.0.9

        Assert.Equal("10.0.0.3", first[3].ToString());
        Assert.Equal("10.0.0.7", second[3].ToString());
        Assert.Equal(2U, third.Count);
        Assert.Equal(["10.0.0.8", "10.0.0.9"], TestRanges.Addresses(third));

        // после закрытия витка кольцо в начале диапазона
        Assert.Equal("10.0.0.0", allocator.Allocate(1)[0].ToString());
    }

    [Fact]
    public void Allocate_HugeCount_IsClampedToRemainingAddressesWithoutOverflow()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/30");

        var lease = allocator.Allocate(uint.MaxValue);

        Assert.Equal(4U, lease.Count);
        Assert.Equal(["192.0.2.0", "192.0.2.1", "192.0.2.2", "192.0.2.3"], TestRanges.Addresses(lease));

        foreach (var address in lease)
        {
            Assert.True(allocator.RangeContains(address));
        }

        // следующая аренда снова идёт с начала, а не повторяет хвост
        Assert.Equal("192.0.2.0", allocator.Allocate(1)[0].ToString());
    }
}

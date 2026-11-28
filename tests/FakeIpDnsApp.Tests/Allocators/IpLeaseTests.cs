using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FakeIpDnsApp.Allocators;
using FakeIpDnsApp.Tests.Support;
using Xunit;

namespace FakeIpDnsApp.Tests.Allocators;

/// <summary>
/// IpLease: интервал задаётся парой (начальный адрес, количество); адреса, счёт и конец интервала
/// выводятся арифметикой, ничего кроме этих двух полей не хранится.
/// </summary>
public sealed class IpLeaseTests
{
    [Fact]
    public void Indexer_ReturnsContiguousAddressesOfInterval()
    {
        var lease = TestRanges.Ipv4("192.0.2.10-192.0.2.12").Allocate(3);

        Assert.Equal("192.0.2.10", lease[0].ToString());
        Assert.Equal("192.0.2.11", lease[1].ToString());
        Assert.Equal("192.0.2.12", lease[2].ToString());
    }

    [Fact]
    public void Indexer_ThrowsOutsideInterval()
    {
        var lease = TestRanges.Ipv4("192.0.2.10-192.0.2.12").Allocate(3);

        Assert.Throws<ArgumentOutOfRangeException>(() => lease[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease[-1]);
        // (uint)index >= Count: отрицательный индекс превращается в огромное число
        Assert.Throws<ArgumentOutOfRangeException>(() => lease[int.MinValue]);
    }

    [Fact]
    public void Empty_EnumeratesNothingAndCarriesNoAddresses()
    {
        var empty = IpLease<uint>.Empty;

        Assert.Equal(0U, empty.Count);
        Assert.Empty(empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty[0]);
    }

    [Fact]
    public void Enumeration_MatchesIndexerAndReportsSameCount()
    {
        var lease = TestRanges.Ipv4("192.0.2.0/29").Allocate(5);

        Assert.Equal([lease[0], lease[1], lease[2], lease[3], lease[4]], [.. lease]);

        IReadOnlyCollection<IPAddress> asCollection = lease;

        Assert.Equal(5, asCollection.Count);
        Assert.Equal(5, lease.Count()); // LINQ по IReadOnlyList
    }

    [Fact]
    public void Zip_PairsFakeAddresssWithRealOnesByPosition()
    {
        var lease = TestRanges.Ipv4("192.0.2.0/29").Allocate(3);
        IPAddress[] real =
        [
            IPAddress.Parse("203.0.113.1"),
            IPAddress.Parse("203.0.113.2"),
            IPAddress.Parse("203.0.113.3")
        ];

        var mappings = lease.Zip(real).ToArray();

        Assert.Equal(3, mappings.Length);
        Assert.Equal("192.0.2.0", mappings[0].First.ToString());
        Assert.Equal("203.0.113.1", mappings[0].Second.ToString());
        Assert.Equal("192.0.2.2", mappings[2].First.ToString());
        Assert.Equal("203.0.113.3", mappings[2].Second.ToString());
    }

    [Fact]
    public void Zip_TruncatesToShorterSide_AsAppDoesWhenRangeIsSmallerThanAnswer()
    {
        var lease = TestRanges.Ipv4("192.0.2.0-192.0.2.1").Allocate(2); // 2 адреса
        IPAddress[] real = [IPAddress.Parse("203.0.113.1"), IPAddress.Parse("203.0.113.2"), IPAddress.Parse("203.0.113.3")];

        Assert.Equal(2, lease.Zip(real).Count());
    }

    [Fact]
    public void LastAddressOfInterval_EqualsStartAddressPlusCountMinusOne()
    {
        var lease = TestRanges.Ipv4("10.0.0.0-10.0.0.99").Allocate(100);

        Assert.Equal(100U, lease.Count);
        Assert.Equal(100, TestRanges.Sorted(lease).Distinct().Count());
        Assert.Equal("10.0.0.99", lease[99].ToString());
    }

    [Fact]
    public void CollectionCount_IsSafeInt_ForLargestAllowedLease()
    {
        // аллокатор не принимает диапазон крупнее int.MaxValue адресов,
        // поэтому Count аренды всегда помещается в int (IReadOnlyCollection<IPAddress>.Count)
        var allocator = TestRanges.Ipv4("0.0.0.0-127.255.255.254"); // ровно int.MaxValue адресов

        var lease = allocator.Allocate(uint.MaxValue);

        IReadOnlyCollection<IPAddress> asCollection = lease;

        Assert.Equal((uint)int.MaxValue, lease.Count);
        Assert.Equal(int.MaxValue, asCollection.Count);
    }

    [Fact]
    public void EmptyLease_IsTheSameValueRegardlessOfAllocator()
    {
        Assert.Equal(IpLease<uint>.Empty, TestRanges.Ipv4("192.0.2.0/30").Allocate(0));
        Assert.Equal(0U, IpLease<ulong>.Empty.Count);
    }
}

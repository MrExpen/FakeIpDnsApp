using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FakeIpDnsApp.Tests.Support;
using Xunit;

namespace FakeIpDnsApp.Tests.Allocators;

/// <summary>
/// Lock-free кольцо (Volatile.Read + Interlocked.CompareExchange) под параллельными PostProcessAsync:
/// ни одного адреса вне диапазона, ни одной потерянной или выданной дважды аренды в один виток.
/// </summary>
public sealed class BaseIpAllocatorConcurrencyTests
{
    private const int RangeSize = 1024;
    private const int Threads = 16;

    [Fact]
    public void Allocate_ParallelSingleAddressLeases_IssueWholeCycleWithoutDuplicates()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/22"); // 1024 адреса
        var issued = new ConcurrentBag<IPAddress>();

        Parallel.For(0, RangeSize, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            issued.Add(allocator.Allocate()[0]);
        });

        Assert.Equal(RangeSize, issued.Count);
        Assert.Equal(RangeSize, issued.Distinct().Count());
        Assert.Contains(IPAddress.Parse("192.0.2.0"), issued);
        Assert.Contains(IPAddress.Parse("192.0.2.255"), issued);
    }

    [Fact]
    public void Allocate_ParallelVariableSizeLeases_NeverLeaveTheRange()
    {
        var allocator = TestRanges.Ipv4("198.18.0.0-198.18.0.127"); // 128 адресов
        var outside = new ConcurrentBag<string>();
        var counts = new ConcurrentBag<uint>();

        Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = Threads }, i =>
        {
            var lease = allocator.Allocate((uint)(i % 5 + 1));

            counts.Add(lease.Count);

            foreach (var address in lease)
            {
                if (!allocator.RangeContains(address))
                {
                    outside.Add(address.ToString());
                }
            }
        });

        Assert.Empty(outside);
        Assert.Equal(2000, counts.Count);
        Assert.True(counts.Min() >= 1);
        Assert.True(counts.Max() <= 5);
    }

    [Fact]
    public void TryReturn_Parallel_AtMostOneCallerRollsTheRingBack()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/24");
        var lease = allocator.Allocate(2);
        var results = new bool[Threads];

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, i =>
        {
            results[i] = allocator.TryReturn(lease);
        });

        // CAS по одному счётчику: откатить кольцо может только один вызов
        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(["192.0.2.0", "192.0.2.1"], TestRanges.Addresses(allocator.Allocate(2)));
    }

    [Fact]
    public void Allocate_Parallel_MovesRingExactlyOneStepPerLease_EveryAddressIssuedSameNumberOfTimes()
    {
        var allocator = TestRanges.Ipv4("192.0.2.0/22"); // 1024 адреса
        var issued = new ConcurrentBag<IPAddress>();
        const int allocations = RangeSize * 4;

        Parallel.For(0, allocations, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            issued.Add(allocator.Allocate()[0]);
        });

        // каждый успешный CAS сдвигает кольцо ровно на один адрес, ретраи счётчик не тратят
        var groups = issued.GroupBy(x => x).ToArray();

        Assert.Equal(RangeSize, groups.Length);

        foreach (var group in groups)
        {
            Assert.Equal(4, group.Count());
        }
    }
}

using System.Collections.Generic;

namespace FakeIpRouting;

public class IpAllocatorFactory
{
    private readonly Dictionary<IPAddressRange, IpAllocator> Allocators = new();

    public IpAllocator? Create(string? range)
    {
        if (range is null)
        {
            return null;
        }

        return Create(IPAddressRange.Parse(range));
    }

    public IpAllocator Create(IPAddressRange range)
    {
        if (Allocators.TryGetValue(range, out var ipAllocator))
        {
            return ipAllocator;
        }

        ipAllocator = new IpAllocator(range);
        Allocators.Add(range, ipAllocator);
        return ipAllocator;
    }

    public void AddRange(IEnumerable<IpAllocator> allocators)
    {
        foreach (var ipAllocator in allocators)
        {
            TryAdd(ipAllocator);
        }
    }

    public bool TryAdd(IpAllocator? allocator)
    {
        if (allocator is null)
        {
            return false;
        }

        return Allocators.TryAdd(allocator.Range, allocator);
    }
}
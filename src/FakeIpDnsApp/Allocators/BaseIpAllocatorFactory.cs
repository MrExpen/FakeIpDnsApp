using System.Collections.Generic;
using System.Numerics;

namespace FakeIpRouting.Allocators;

public abstract class BaseIpAllocatorFactory<TAllocator, TUint> where TAllocator : BaseIpAllocator<TUint>
    where TUint : struct, IBinaryInteger<TUint>
{
    private readonly Dictionary<IPAddressRange, TAllocator> Allocators = new();

    public TAllocator? Create(string? range)
    {
        if (range is null)
        {
            return null;
        }

        return Create(IPAddressRange.Parse(range));
    }

    public TAllocator Create(IPAddressRange range)
    {
        if (Allocators.TryGetValue(range, out var ipAllocator))
        {
            return ipAllocator;
        }

        ipAllocator = InternalCreate(range);
        Allocators.Add(range, ipAllocator);
        return ipAllocator;
    }

    protected abstract TAllocator InternalCreate(IPAddressRange range);

    public void AddRange(IEnumerable<TAllocator> allocators)
    {
        foreach (var ipAllocator in allocators)
        {
            TryAdd(ipAllocator);
        }
    }

    public bool TryAdd(TAllocator? allocator)
    {
        if (allocator is null)
        {
            return false;
        }

        return Allocators.TryAdd(allocator.Range, allocator);
    }
}
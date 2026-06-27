using System.Collections.Generic;
using System.Linq;

namespace FakeIpRouting.Extensions;

public static class EnumerableExtensions
{
    public static IEnumerable<T> TakeIfNotZero<T>(this IEnumerable<T> enumerable, int count)
    {
        return count == 0 ? enumerable : enumerable.Take(count);
    }
}
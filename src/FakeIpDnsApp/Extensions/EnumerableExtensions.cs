using System.Collections.Generic;
using System.Linq;

namespace FakeIpDnsApp.Extensions;

public static class EnumerableExtensions
{
    public static IEnumerable<T> TakeIfNotZero<T>(this IEnumerable<T> enumerable, int count)
    {
        return count == 0 ? enumerable : enumerable.Take(count);
    }

    public static bool IsSingle<T>(this IEnumerable<T> enumerable)
    {
        using var enumerator = enumerable.GetEnumerator();

        return enumerator.MoveNext() && !enumerator.MoveNext();
    }
}
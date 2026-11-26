using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Numerics;

namespace FakeIpDnsApp.Allocators;

/// <summary>
/// Аренда интервала адресов, выданная <see cref="BaseIpAllocator{TUint}"/>. Хранит только начальный адрес
/// интервала и количество адресов: и сами адреса, и конец интервала, и значения для возврата в пул
/// <see cref="BaseIpAllocator{TUint}.TryReturn"/> получают арифметикой из этих двух полей.
/// Интервал всегда целиком лежит внутри диапазона аллокатора, поэтому следующий адрес - это
/// <c>StartAddress + индекс</c>.
/// </summary>
/// <param name="StartAddress">Числовое значение начального адреса интервала.</param>
/// <param name="Count">Количество идущих подряд адресов интервала.</param>
public readonly record struct IpLease<TUint>(TUint StartAddress, uint Count)
    : IReadOnlyList<IPAddress>
    where TUint : struct, IBinaryInteger<TUint>
{
    /// <summary>
    /// Аренда нулевой длины: ни одного адреса не занято, вернуть в пул нечего.
    /// </summary>
    public static readonly IpLease<TUint> Empty = new(TUint.Zero, 0U);

    /// <summary>
    /// Адрес аренды по индексу интервала: <c>lease[0]</c> - начало интервала, <c>lease[Count - 1]</c> -
    /// его конец. Как у <c>List&lt;T&gt;</c>: выход за границу интервала -
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public IPAddress this[int index]
    {
        get
        {
            if ((uint)index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return GetAddress((uint)index);
        }
    }

    int IReadOnlyCollection<IPAddress>.Count => (int)Count;

    /// <summary>
    /// Адреса интервала по порядку, без хранения списка: каждый считается из начального адреса.
    /// </summary>
    public IEnumerator<IPAddress> GetEnumerator()
    {
        for (uint index = 0; index < Count; index++)
        {
            yield return GetAddress(index);
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private IPAddress GetAddress(uint index)
    {
        return IpAddressMath.ToAddress(unchecked(StartAddress + TUint.CreateTruncating(index)));
    }
}

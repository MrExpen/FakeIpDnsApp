using System;
using System.Net;
using System.Numerics;
using System.Threading;

namespace FakeIpDnsApp.Allocators;

public abstract class BaseIpAllocator<TUint> where TUint : struct, IBinaryInteger<TUint>
{
    public IPAddressRange Range => new(IpAddressMath.ToAddress(RangeStart), RangeSize);

    private readonly TUint RangeStart;
    private readonly uint RangeSize;

    /// <summary>
    /// Позиция кольца: индекс следующего адреса внутри диапазона, всегда в <c>[0 .. RangeSize)</c>.
    /// Изменится только через <see cref="Interlocked"/> и <see cref="Volatile"/>.
    /// </summary>
    private uint Counter;

    protected BaseIpAllocator(IPAddressRange range)
    {
        RangeStart = IpAddressMath.ToInt<TUint>(range.Begin);
        if (range.AddressCount > uint.MaxValue)
        {
            throw new NotSupportedException("Ip range is too big. For ipv6 supported cidr >= 96");
        }

        RangeSize = (uint)range.AddressCount;


        Counter = 0U;
    }

    /// <summary>
    /// Арендует один адрес из пула.
    /// </summary>
    public IpLease<TUint> Allocate()
    {
        return Allocate(1);
    }

    /// <summary>
    /// Арендует <paramref name="count"/> идущих подряд адресов из пула.
    /// Аренда возвращается в пул только явно через <see cref="TryReturn"/>.
    /// Интервал аренды всегда целиком лежит внутри диапазона: если до его конца адресов не хватает,
    /// интервал придвигается к концу диапазона, а следующая аренда начинается с его начала.
    /// Если адресов просят больше, чем их в диапазоне, выдаётся весь диапазон один раз, без повторов.
    /// </summary>
    public IpLease<TUint> Allocate(uint count)
    {
        if (count == 0)
        {
            return IpLease<TUint>.Empty;
        }

        if (count > RangeSize)
        {
            count = RangeSize;
        }

        while (true)
        {
            var current = Volatile.Read(ref Counter);
            var start = current;
            var end = (ulong)current + count;

            if (end > RangeSize)
            {
                // Интервал придвигается к концу диапазона, чтобы не выходить за его границу,
                // а следующая аренда начинается заново с его начала.
                start = RangeSize - count;
                end = RangeSize;
            }

            // Позиция кольца всегда внутри диапазона: конец интервала, дошедший до конца, переносит
            // следующую аренду в его начало.
            if (Interlocked.CompareExchange(ref Counter, (uint)(end % RangeSize), current) == current)
            {
                return new IpLease<TUint>(RangeStart + TUint.CreateTruncating(start), count);
            }
        }
    }

    /// <summary>
    /// Возвращает аренду в пул, чтобы её адреса были выданы заново. Используется, когда маппинг не получился
    /// и адреса достались впустую.
    /// Конец интервала аренды нигде не хранится: это <c>начало аренды + её количество</c>. Возврат удаётся
    /// только если кольцо пула всё ещё стоит на этом конце, то есть после нас никто ничего не арендовал, -
    /// тогда <c>CompareExchange</c> откатывает позицию на начало интервала. Если кто-то уже арендовал адреса
    /// дальше, позиция другая, CAS проваливается и адрес возвращать нельзя, потому что он уже занят.
    /// </summary>
    /// <returns><c>true</c>, если пул откатился и аренда вернулась; <c>false</c> для пустой аренды, аренды не
    /// из этого диапазона или уже занятой кем-то другим.</returns>
    public bool TryReturn(IpLease<TUint> lease)
    {
        if (lease.Count == 0 || lease.Count > RangeSize)
        {
            return false;
        }

        if (!ContainsAddress(lease.StartAddress))
        {
            return false;
        }

        var startPosition = uint.CreateTruncating(lease.StartAddress - RangeStart);
        var endPosition = (uint)(((ulong)startPosition + lease.Count) % RangeSize);

        return Interlocked.CompareExchange(ref Counter, startPosition, endPosition) == endPosition;
    }

    public virtual bool RangeContains(IPAddress address)
    {
        return ContainsAddress(IpAddressMath.ToInt<TUint>(address));
    }

    private bool ContainsAddress(TUint address)
    {
        // Беззнаковое вычитание: адрес ниже RangeStart даёт огромное значение и не проходит проверку.
        return address - RangeStart < TUint.CreateTruncating(RangeSize);
    }
}

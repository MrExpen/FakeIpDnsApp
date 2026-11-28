using System;
using System.Net;
using System.Numerics;
using System.Threading;

namespace FakeIpDnsApp.Allocators;

public abstract class BaseIpAllocator<TUint> where TUint : struct, IBinaryInteger<TUint>
{
    /// <summary>
    /// Максимальный размер диапазона: <see cref="int.MaxValue"/> адресов.
    /// Лимит нужен не только счётчику кольца: количество адресов аренды уходит в <c>int</c>
    /// (<see cref="IpLease{TUint}"/> как <see cref="System.Collections.Generic.IReadOnlyCollection{T}"/>,
    /// индексы в <c>int</c>, длины ответов DNS).
    /// Крупнейший CIDR, который ещё проходит: <c>/2</c> для IPv4 и <c>/98</c> для IPv6 -
    /// <c>/1</c> и <c>/97</c> это уже 2^31 адресов, на один больше лимита.
    /// </summary>
    public const int MaxRangeSize = int.MaxValue;

    public IPAddressRange Range => new(IpAddressMath.ToAddress(RangeStart), RangeSize);

    private readonly TUint RangeStart;

    /// <summary>Количество адресов в диапазоне, никогда не больше <see cref="MaxRangeSize"/>.</summary>
    private readonly uint RangeSize;

    /// <summary>
    /// Позиция кольца: индекс следующего адреса внутри диапазона, всегда в <c>[0 .. RangeSize)</c>.
    /// Изменится только через <see cref="Interlocked"/> и <see cref="Volatile"/>.
    /// </summary>
    private uint Counter;

    /// <summary>
    /// Подготавливает аллокатор диапазона.
    /// </summary>
    /// <exception cref="NotSupportedException">Диапазон крупнее <see cref="MaxRangeSize"/> адресов.</exception>
    protected BaseIpAllocator(IPAddressRange range)
    {
        RangeStart = IpAddressMath.ToInt<TUint>(range.Begin);

        if (range.AddressCount > MaxRangeSize)
        {
            // Крупнее int.MaxValue адресов аллокатор не берётся, см. MaxRangeSize.
            throw new NotSupportedException(
                $"Ip range is too big: {range.AddressCount} addresses, supported at most {MaxRangeSize}. " +
                "IPv4 requires prefix /2 or longer, IPv6 requires prefix /98 or longer.");
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
    /// Интервал аренды всегда целиком лежит внутри диапазона и никогда не пересекается с уже выданным:
    /// если до конца диапазона осталось меньше адресов, чем просят, арендуется остаток, а следующая аренда
    /// начинается с начала диапазона. То есть виток кольца для <c>count</c>, не делящего размер диапазона,
    /// состоит из аренд разного размера, но каждый адрес диапазона выдаётся ровно один раз за виток.
    /// </summary>
    public IpLease<TUint> Allocate(uint count)
    {
        if (count == 0)
        {
            return IpLease<TUint>.Empty;
        }

        while (true)
        {
            var current = Volatile.Read(ref Counter);

            // Хвост диапазона может быть короче запрошенного интервала: берём ровно сколько осталось.
            // Придвигать интервал к концу диапазона (start = RangeSize - count) нельзя - он повторно
            // выдаёт адреса предыдущей аренды, и один фейковый адрес оказывается занят сразу двумя доменами.
            var length = Math.Min(count, RangeSize - current);
            var end = current + length; // всегда <= RangeSize, переполнения uint нет

            // Позиция кольца всегда внутри диапазона: интервал, дошедший до конца, переносит
            // следующую аренду в его начало.
            if (Interlocked.CompareExchange(ref Counter, end == RangeSize ? 0U : end, current) == current)
            {
                return new IpLease<TUint>(RangeStart + TUint.CreateTruncating(current), length);
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

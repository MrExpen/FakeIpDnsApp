using System;

namespace FakeIpRouting;

public readonly struct DomainLevelEnumerable(string domain)
{
    public Enumerator GetEnumerator() => new(domain);

    public struct Enumerator(string domain)
    {
        private int Index = 0;
        private bool FirstIteration = true;

        public bool MoveNext()
        {
            if (FirstIteration)
            {
                FirstIteration = false;
                return true;
            }

            Index = domain.IndexOf('.', Index) + 1;
            return Index > 0;
        }

        public void Reset()
        {
            Index = 0;
            FirstIteration = true;
        }

        public ReadOnlySpan<char> Current => domain.AsSpan(Index);
    }
}
namespace FakeIpDnsApp.Extensions;

public static class DomainExtensions
{
    public static DomainLevelEnumerable EnumerateDomainLevels(this string domain)
    {
        return new DomainLevelEnumerable(domain);
    }
}
using DnsServerCore.ApplicationCommon;

namespace FakeIpDnsApp.Services;

public class DnsServerLogger : ILogger
{
    private readonly IDnsServer DnsServer;

    public DnsServerLogger(IDnsServer dnsServer)
    {
        DnsServer = dnsServer;
    }

    public void WriteLog(string message)
    {
        DnsServer.WriteLog(message);
    }
}
namespace FakeIpDnsApp.Services;

public interface ILogger
{
    /// <summary>
    /// Writes a log entry to the DNS server log file.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public void WriteLog(string message);
}
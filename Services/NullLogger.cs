namespace FakeIpRouting.Services;

public class NullLogger : ILogger
{
    public static NullLogger Instance { get; } = new();

    public void WriteLog(string message)
    {
    }
}
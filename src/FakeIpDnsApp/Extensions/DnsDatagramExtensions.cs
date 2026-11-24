using System.IO;
using System.Text.Json;
using TechnitiumLibrary.Net.Dns;

namespace FakeIpRouting.Extensions;

public static class DnsDatagramExtensions
{
    extension(DnsDatagram datagram)
    {
        public string GetJsonSerializedString()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            using var reader = new StreamReader(ms);
            
            datagram.SerializeTo(writer);
            writer.Flush();
            
            ms.Seek(0, SeekOrigin.Begin);

            return reader.ReadToEnd();
        }
    }
}
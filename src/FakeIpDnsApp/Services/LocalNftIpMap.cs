using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FakeIpDnsApp.Services;

public sealed class LocalNftIpMap : IIpMap
{
    private const string NFT = "nft";

    private readonly ILogger Logger;

    private readonly string Ipv4TableName;
    private readonly string Ipv6TableName;

    private readonly string Ipv4MapName;
    private readonly string Ipv6MapName;

    public LocalNftIpMap(
        ILogger logger,
        string ipv4TableName,
        string ipv6TableName,
        string ipv4MapName,
        string ipv6MapName
    )
    {
        Ipv4TableName = ipv4TableName;
        Ipv6TableName = ipv6TableName;
        Ipv4MapName = ipv4MapName;
        Ipv6MapName = ipv6MapName;
        Logger = logger;
    }

    public async Task CreateMappingsAsync(
        IReadOnlyCollection<KeyValuePair<IPAddress, IPAddress>> mappings,
        uint ttl,
        CancellationToken cancellationToken
    )
    {
        if (mappings.Any(x =>
                x.Key.AddressFamily != x.Value.AddressFamily ||
                x.Key.AddressFamily is not AddressFamily.InterNetwork and not AddressFamily.InterNetworkV6))
        {
            throw new InvalidOperationException("invalid address family.");
        }

        if (mappings.Count == 0)
        {
            return;
        }

        Logger.WriteLog(
            $"Create mapping [{string.Join(", ", mappings.Select(x => x.Key.ToString()))}] -> [{string.Join(", ", mappings.Select(x => x.Value.ToString()))}]");

        await DeleteMappings(mappings.Select(x => x.Key));

        var addressFamilies = mappings.Select(x => x.Key.AddressFamily).Distinct().ToArray();

        if (addressFamilies.Length > 1)
        {
            var createIpv4Task = CreateMappingsAsync(true,
                mappings.Where(x => x.Key.AddressFamily == AddressFamily.InterNetwork));
            var createIpv6Task = CreateMappingsAsync(true,
                mappings.Where(x => x.Key.AddressFamily == AddressFamily.InterNetworkV6));

            await Task.WhenAll(createIpv4Task, createIpv6Task);
            return;
        }

        await CreateMappingsAsync(addressFamilies[0] == AddressFamily.InterNetwork, mappings);
    }

    private Task DeleteMappings(IEnumerable<IPAddress> fake)
    {
        return Task.WhenAll(fake.Select(DeleteMapping));
    }

    private async Task DeleteMapping(IPAddress fake)
    {
        var psi = new ProcessStartInfo(NFT, GetDeleteArguments(fake))
        {
            RedirectStandardError = true,
        };

        using var deleteProcess = Process.Start(psi);

        // stderr вычитывается до ожидания выхода: переполненный буфер pipe остановил бы nft,
        // а WaitForExitAsync не вернулся бы никогда. Ошибку удаления не проверяем:
        // удалить несуществующий элемент - штатная ситуация.
        var errorTask = deleteProcess!.StandardError.ReadToEndAsync();

        await deleteProcess.WaitForExitAsync(CancellationToken.None);

        await errorTask;
    }

    private async Task CreateMappingsAsync(bool ipv4, IEnumerable<KeyValuePair<IPAddress, IPAddress>> mappings)
    {
        var arguments = GetAddArguments(ipv4, mappings);
        var psi = new ProcessStartInfo(NFT, arguments)
        {
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)!;

        // stderr читается параллельно ожиданию: иначе длинное сообщение об ошибке заполняет буфер pipe,
        // nft блокируется на записи, а WaitForExitAsync ждёт навсегда
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(CancellationToken.None);

        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"couldn't map fake ip address. ErrorCode: {process.ExitCode}. {error}");
        }
    }

    private IEnumerable<string> GetDeleteArguments(IPAddress fake)
    {
        return fake.AddressFamily switch
        {
            AddressFamily.InterNetwork => ["delete", "element", "ip", Ipv4TableName, Ipv4MapName, $"{{ {fake} }}"],
            AddressFamily.InterNetworkV6 => ["delete", "element", "ip6", Ipv6TableName, Ipv6MapName, $"{{ {fake} }}"],
            _ => throw new NotSupportedException(),
        };
    }

    private IEnumerable<string> GetAddArguments(bool ipv4, IEnumerable<KeyValuePair<IPAddress, IPAddress>> mappings)
    {
        var map = string.Join(", ", mappings.Select(x => $"{x.Key} : {x.Value}"));

        return
        [
            "add",
            "element",
            ipv4 ? "ip" : "ip6",
            ipv4 ? Ipv4TableName : Ipv6TableName,
            ipv4 ? Ipv4MapName : Ipv6MapName,
            $"{{ {map} }}"
        ];
    }

    public void Dispose()
    {
        // Do nothing
    }
}
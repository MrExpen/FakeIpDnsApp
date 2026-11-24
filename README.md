# FakeIpDnsApp

DNS-приложение (DNS App) для [Technitium DNS Server](https://technitium.com/dns/): выдаёт домену «фейковый» IP из
заданного диапазона и публикует пару *фейковый → реальный* адрес в файрвол, чтобы дальше развести трафик по нужным
маршрутам (VPN, балансировщик, прокси).

Приложение работает как DNS post-processor: сервер резолвит домен обычным способом, а FakeIpDnsApp подменяет ответ
адресами из диапазонов и обновляет маппинг в `nft` (или в удалённом агенте по gRPC).

Проект вынесен в отдельный репозиторий: он собирается **без** исходников Technitium DNS Server, ссылки на хостовые
сборки нужны только на время компиляции.

## Требования

- Technitium DNS Server 15.x, собранный под .NET 10 (приложение целится в `net10.0` и использует C# 14).
- Для режима `nft` (без `grpcAgentAddress`): Linux, `nft` в `PATH`, процесс DNS-сервера с правами на изменение
  правил (`root` или `CAP_NET_ADMIN`). На Windows этот режим не работает — используйте gRPC-агента.
- Для режима gRPC: отдельно запущенный агент, реализующий сервис из `src/FakeIpDnsApp/Protos/ipMap.proto`.

## Сборка

```powershell
# 1. один раз: скачать и собрать сборки хоста, они попадут в libs\ (в git не попадают)
.\build\get-libs.ps1

# 2. обычная сборка
dotnet build FakeIpDnsApp.sln -c Release

# 3. архив для установки в DNS Server
.\build\package.ps1     # -> dist\FakeIpDnsApp-<version>.zip
```

### Версии зависимостей (`versions.json`)

```json
{
    "dnsServerTag": "v15.2.0",
    "technitiumLibraryTag": "dns-server-v15.2.0",
    "targetFramework": "net10.0"
}
```

Здесь лежат полные имена тегов upstream-репозиториев — ровно такие, какие они в самих репозиториях, скрипт ничего
не придумывает и не склеивает. У TechnitiumLibrary своей нумерации нет: его теги названы по релизу DNS Server,
поэтому в его теге уже сидит префикс `dns-server-`. Оба тега должны относиться к одному релизу и к той версии
DNS Server, на которую вы ставите приложение.

```powershell
.\build\get-libs.ps1                          # теги из versions.json
.\build\get-libs.ps1 -DryRun                  # показать эти теги и проверить, что они есть upstream
.\build\get-libs.ps1 -ReleaseVersion v15.3.0  # сдвинуть оба тега на другой релиз одним параметром
.\build\get-libs.ps1 -DnsServerTag v15.3.0    # отдельно взять другой тег DnsServer
.\build\get-libs.ps1 -LibraryTag dns-server-v15.1.0   # отдельно взять другой тег TechnitiumLibrary
```

`-ReleaseVersion v15.3.0` меняет только номер версии внутри каждого тега: `v15.2.0` → `v15.3.0`, а
`dns-server-v15.2.0` → `dns-server-v15.3.0` (префикс сохраняется).

Имя тега проверяется по маске (опциональный строчный префикс + `vMAJOR.MINOR.PATCH`), несуществующий тег
сообщается до клонирования со списком последних тегов репозитория. Если репозиторий уже скачан в `.deps\`, скрипт
добирает нужный тег и переключает рабочую копию на него — старая версия по ошибке не останется.

После `get-libs.ps1` в `libs\` лежат три DLL: `DnsServerCore.ApplicationCommon.dll`, `TechnitiumLibrary.dll`,
`TechnitiumLibrary.Net.dll`.

Все три ссылки в `.csproj` объявлены как `<Reference>` с `Private=false`: DLL нужны только чтобы скомпилироваться.
В рантайме их предоставляет сам DNS Server. **Никогда не кладите эти DLL рядом с DLL приложения** — DNS-сервер
загрузит их повторно в контекст приложения, и проверка «это ли `IDnsApplication`» перестанет сходиться.

## Установка

1. Соберите архив (`.\build\package.ps1`) — внутри всё лежит плоско: `FakeIpDnsApp.dll`, `FakeIpDnsApp.deps.json`,
   NuGet-зависимости и `dnsApp.config`.
2. В веб-консоли DNS Server откройте **Settings → Apps** и установите приложение из архива.
   Либо вручную: распакуйте содержимое архива в `<config>/apps/FakeIpDnsApp/` и перезапустите сервер.

Имя папки приложения = имя приложения. Оно должно совпадать с именем DLL (`FakeIpDnsApp.dll`), иначе загрузчик
приложение не найдёт. Прежнее имя `FakeIpRouting` больше не используется: при переходе с него установите приложение
под новым именем и перенесите настройки из `<config>/apps/FakeIpRouting/dnsApp.config` вручную — обновление
приложения конфигурационный файл не перезаписывает.

## Конфигурация (`dnsApp.config`)

```json
{
    "disabled": false,
    "logging": false,
    "cache": true,
    "configs": [
        {
            "disabled": false,
            "ipV4Range": "198.18.0.0/15",
            "maxIpv4AddressesPerDomain": 2,
            "ipV6Range": "2a04:5201:4:1::/110",
            "maxIpv6AddressesPerDomain": 3,
            "domains": []
        }
    ],
    "grpcAgentAddress": "http://127.0.0.1:5000",
    "ipv4TableName": "vpn",
    "ipv6TableName": "vpn",
    "ipv4MapName": "fakeip_map",
    "ipv6MapName": "fakeip6_map",
    "maxTtl": 3600,
    "ignoreCase": true
}
```

| Ключ | Значение по умолчанию | Что делает |
| --- | --- | --- |
| `disabled` | `false` | Выключает приложение целиком (ответ проходит без подмены). |
| `cache` | `true` | Разрешает серверу кэшировать изменённый ответ. |
| `configs[]` | — | Список диапазонов. Элементы с `disabled: true` пропускаются. |
| `configs[].ipV4Range`, `ipV6Range` | — | CIDR (`198.18.0.0/15`) или диапазон (`198.18.0.0-198.19.255.255`). |
| `configs[].maxIpv4AddressesPerDomain`, `maxIpv6AddressesPerDomain` | `1` | Сколько фейковых адресов выдать на один домен. |
| `configs[].domains[]` | `[]` | Домены этого диапазона; `[]` — все домены, не описанные в других диапазонах. |
| `grpcAgentAddress` | `null` | Если задан — маппинг уходит gRPC-агенту, иначе локальный `nft`. |
| `ipv4TableName`, `ipv6TableName` | `vpn` | Имена `nft`-таблиц (`table inet <name>`). |
| `ipv4MapName`, `ipv6MapName` | `fakeip_map`, `fakeip6_map` | Имена `nft`-map внутри этих таблиц. |
| `maxTtl` | `3600` | TTL в ответе и время жизни маппинга в таблицах. |
| `ignoreCase` | `true` | Регистронезависимый поиск домена в диапазонах. |

Диапазоны лучше брать из reserved-пространства (`198.18.0.0/15` — RFC 2544, `2a04:5201::/29` и т. п.), чтобы они не
пересекались с реальными сетями.

## Как это устроено

- `App.cs` — точка входа: реализует `IDnsApplication` и `IDnsPostProcessor`.
- `Allocators/` — выдача адресов из диапазона и возврат их по TTL.
- `Services/LocalNftIpMap.cs` — ведёт `nft`-таблицы через `nft` CLI (`add`/`flush`).
- `Services/GrpcIpMap.cs` — то же самое через gRPC-агента (для DNS-сервера на Windows или удаленного файрвола).
- `Extensions/`, `IPAddressRange.cs`, `RangeConfig.cs` — разбор диапазонов и работа с доменами.

## Лицензия

GPL-3.0-only, как и Technitium DNS Server, чьи контракты (`DnsServerCore.ApplicationCommon`) используются для
компиляции. Подробности — в `LICENSE`.

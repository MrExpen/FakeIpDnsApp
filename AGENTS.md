# AGENTS.md

Инструкция для агентов, работающих в этом репозитории. Читать вместе с `README.md` (там про диапазоны и
установку) и `CHANGELOG.md` (там про историю решений).

## Что это за проект

`FakeIpDnsApp` — **DNS App (плагин) для Technitium DNS Server 15.x**, post-processor: сервер резолвит домен
обычно, приложение подменяет A/AAAA-ответ адресами из «фейкового» диапазона и публикует пару
*фейковый → реальный* в `nft`-map (локально) или gRPC-агенту.

Репозиторий standalone: исходники Technitium в сборку не входят. `DnsServerCore.ApplicationCommon`,
`TechnitiumLibrary`, `TechnitiumLibrary.Net` — **только ссылки на время компиляции**; в рантайме их предоставляет
процесс DNS-сервера.

Рабочая ветка — `dev`, origin `git@github.com:MrExpen/FakeIpDnsApp.git`. Лицензия — GPL-3.0-only (`LICENSE`).

## Команды

| Что нужно сделать | Команда |
| --- | --- |
| Первый запуск / поднять версию хоста | `.\build\get-libs.ps1` |
| Просто проверить, что теги есть upstream | `.\build\get-libs.ps1 -DryRun` |
| Сдвинуть оба тега на другой релиз | `.\build\get-libs.ps1 -ReleaseVersion v15.6.1` |
| Взять другой тег отдельно | `-DnsServerTag v15.6.1` / `-LibraryTag dns-server-v15.6.1` |
| Сборка | `dotnet build FakeIpDnsApp.sln -c Release` |
| Тесты аллокаторов и диапазонов | `dotnet test FakeIpDnsApp.sln -c Release` |
| Архив для установки | `.\build\package.ps1` (`-SkipBuild`, `-Version 4.2.9`) |
| Собираться против установленного сервера | `dotnet build -p:LibDir="C:\Program Files\DnsServerApp"` |

Тесты **есть** (`dotnet test FakeIpDnsApp.sln -c Release`, проект `tests/FakeIpDnsApp.Tests`, xunit): кольцо
аллокатора, `IpLease`, фабрики аллокаторов и `IPAddressRange`. Покрытые тестами модули не ссылаются на хостовые
DLL, поэтому тесты собираются без `libs\`. CI **нет**. Проверка изменений = тесты + чистая сборка +
`package.ps1` + прогон на сервере. Сейчас `dotnet build -c Release` (SDK 10.0.301, `libs\` заполнен) даёт
**0 warnings, 0 errors**; держите эту планку.

`App.cs`, `Services/` и `Protos/` тестами не покрыты: `App` требует `IDnsServer`, `LocalNftIpMap` требует `nft`, а
`nft` — это Linux. `GrpcIpMap` проверяется только на сервере с агентом. Полный прогон недоступен с
Windows-машины в режиме `nft`: локально проверяются компиляция, тесты и состав архива.

## Карта репозитория

```
Directory.Build.props      net10.0, C# 14, Nullable, ImplicitUsings=disable, LibDir, плоский путь вывода
versions.json             пины тегов upstream (dnsServerTag, technitiumLibraryTag)
FakeIpDnsApp.sln          основной csproj, тестовый проект и solution folder Scripts
build/get-libs.ps1        клонирует теги в .deps/, собирает их, кладёт DLL в libs/
build/package.ps1         dotnet publish -> dist/FakeIpDnsApp-<version>.zip (плоский)
src/FakeIpDnsApp/
  App.cs                  IDnsApplication + IDnsPostProcessor: разбор конфига и вся логика подмены
  dnsApp.config           поставляемый конфиг (копируется в вывод, попадает в архив)
  RangeConfig.cs          диапазон + лимиты адресов на домен
  IPAddressRange.cs       парсинг CIDR и формата A-B, Equals/GetHashCode (по ним ключ аллокатора)
  DomainLevelEnumerable.cs  итератор уровней домена для поиска по FrozenDictionary
  Allocators/             BaseIpAllocator, Ipv4/Ipv6Allocator (+Factory), IpLease, IpAddressMath
  Extensions/             DomainExtensions, EnumerableExtensions, IpAddressExtensions,
                          DnsDatagramExtensions (extension-участники C# 14)
  Services/               IIpMap, LocalNftIpMap (nft CLI), GrpcIpMap (gRPC), ILogger,
                          DnsServerLogger, NullLogger
  Protos/ipMap.proto      контракт агента (csharp_namespace = IpGrpcMap)
tests/FakeIpDnsApp.Tests/ xunit-тесты: Support/TestRanges.cs (помощники), IPAddressRangeTests.cs и
                          Allocators/ (кольцо, TryReturn, IpLease, фабрики, многопоточность)
libs/                     собранные хостовые DLL (gitignored) — их нужно иметь до первой сборки
.deps/                    клонированные upstream-репозитории по тегам (gitignored)
```

## Gitignored — и почему

`bin/`, `obj/`, `dist/`, `libs/`, `.deps/`, `.idea/` — **ничего из этого не коммитится**.

`.deps\` — полные исходники `DnsServer` и `TechnitiumLibrary` на пин-тегах. Их **можно читать** (контракты:
`.deps\DnsServer\DnsServerCore.ApplicationCommon\*.cs`, `GetPropertyValue`/`ReadArray` — в
`.deps\TechnitiumLibrary\TechnitiumLibrary\JsonExtensions.cs`), но **нельзя править**: `get-libs.ps1` держит там
detached checkout на теге и при смене версии переключает рабочую копию.

## Как устроен поток данных

1. `InitializeAsync(dnsServer, config)` — разбор `dnsApp.config`: создание аллокаторов, выбор `IIpMap`
   (gRPC-агент или локальный `nft`), построение `FrozenDictionary<string,int> домен → индекс диапазона`
   и `AlternateLookup<ReadOnlySpan<char>>`. Существующие аллокаторы **переиспользуются** при перечитывании
   конфига (`Configs` из прошлого вызова добавляются в новые фабрики), чтобы кольцо адресов не сбрасывалось.
2. `PostProcessAsync(...)` — фильтр (`IN`, `StandardQuery`, `NoError`, есть ответ, тип A/AAAA, домен найден),
   затем выдача аренды из аллокатора → `IIpMap.CreateMappingsAsync` → новый `DnsDatagram` с `TTL = min(TTL, maxTtl)`.
3. Если маппинг не создался — `allocator.TryReturn(lease)` и повторное `throw`: адреса возвращаются в пул,
   только если кольцо пула всё ещё стоит на конце этой аренды.
4. `Dispose()` обязан освободить `IpMap` (gRPC-канал).

## Инварианты (ломать нельзя)

1. **Хостовые DLL не едут в архив.** `<Reference>` + `Private=false` в csproj и исключение
   `^(TechnitiumLibrary|DnsServerCore\.)` в `package.ps1`. Если положить их рядом с `FakeIpDnsApp.dll`,
   DNS-сервер загрузит второй экземпляр в контекст плагина и проверка «это ли `IDnsApplication`» перестанет
   сходиться — приложение не загрузится.
2. **Архив плоский**: `FakeIpDnsApp.dll` + `FakeIpDnsApp.deps.json` в корне zip, без `publish/`. Имя папки
   приложения в `<config>/apps/` = имя DLL. Не переименовывать `AssemblyName`/`RootNamespace`.
3. **Только `net10.0` и C# 14.** Все свойства — в `Directory.Build.props`, в csproj их не дублировать.
   `Nullable=enable`, `ImplicitUsings=disable` → каждый `using` пишем явно.
4. **Версия**: `<Version>` в `FakeIpDnsApp.csproj` + запись в `CHANGELOG.md` (Keep a Changelog, семантическая
   нумерация, продолжающая `4.2.6` из форка — иначе DNS Server сочтёт сборку более старой и не обновит
   приложение).
5. **Теги upstream — полные имена из `versions.json`** (`v15.6.2`, `dns-server-v15.6.2`); ничего не склеивается
   и не выводится, кроме `-ReleaseVersion`, который меняет только `vMAJOR.MINOR.PATCH` внутри уже готового тега.
6. **Аллокатор lock-free**: `Volatile.Read` + `Interlocked.CompareExchange` по одному счётчику-кольцу,
   `TryReturn` — best-effort по определению. Не заменять на `lock`/`Monitor`/`ConcurrentQueue`; не хранить
   «конец аренды» — он выводится арифметикой из `IpLease(StartAddress, Count)`. Кольцо выдаёт адреса строго
   последовательно по кругу: интервал никогда не придвигается к концу диапазона (если остатка не хватает,
   арендуется остаток), поэтому один адрес не выдаётся дважды за виток. Проверка — тест
   `Allocate_IssuedAddressesFollowRingOrder_WithoutSkipsOrRepeats`. Диапазон крупнее
   `BaseIpAllocator.MaxRangeSize` (`int.MaxValue` адресов) конструктор отвергает — это держит количество адресов
   аренды внутри `int`.
7. **`nft`-таблицу и maps создаёт оператор.** Приложение делает только `add element` / `delete element`
   (`table inet <ipv4TableName>`, map `<ipv4MapName>`); само создание таблиц сюда не добавлять.
   Параметр `ttl` в `LocalNftIpMap` не используется — время жизни записи в `nft` здесь не задаётся.
8. **Контракт пост-процессора**: `null` = дропнуть запрос, исходный `response` = не подменять. Возврат
   «уже фейкового» ответа (ветки cache-path) обязателен, иначе повторные запросы будут резать уже выданные адреса.
9. **gRPC-контракт**: `fakeIps`/`realIps` — сырые байты адреса (4 для IPv4, 16 для IPv6) через
   `IpAddressExtensions.ToByteString()`, старшие байты первыми. Изменение `ipMap.proto` требует синхронной
   правки агента.

## Конвенции кода

- file-scoped namespaces; один тип на файл; имя файла = имя типа; папки по назначению (`Allocators/`, `Services/`, `Extensions/`).
- Поля и свойства `PascalCase`, параметры и локальные `camelCase`; константы `PascalCase` (`NFT`).
- Расширения типов — `extension`-участники C# 14 в `Extensions/` (`IpAddressExtensions`, `DnsDatagramExtensions`),
  обычные статические расширения — там же.
- `null`-прощение `!` там, где непустоту гарантирует контракт хоста или инвариант (`config!`, `IpMap!`, `config.Ipv4Allocator!`
  под проверкой `Ipv4Configured`); `is not null` фильтрует перед `!` в LINQ.
- Современный синтаксис: коллекционные литералы `[.. ]`, `switch`-выражения, `FrozenDictionary` +
  `AlternateLookup<ReadOnlySpan<char>>` для поиска по домену без аллокаций.
- Язык комментариев: в `App.cs` и `Allocators/` — по-русски, XML-доки и комментарии в скриптах — по-английски.
  Держитесь языка соседнего кода в том же файле.
- Абстракции уже есть: `IIpMap`, `ILogger`, `BaseIpAllocator<TUint>` + фабрики. Новый бэкенд — реализация
  `IIpMap`, новый тип адреса — наследник `BaseIpAllocator` с подходящим `TUint` (`uint`/`UInt128`).

## Конфигурация (`dnsApp.config`)

Ключи и значения по умолчанию — в таблице README. Важные детали:

- В поставляемом `dnsApp.config` стоит `grpcAgentAddress: "http://127.0.0.1:5000"`, то есть **упакованное
  приложение по умолчанию работает в режиме gRPC**. Для локального `nft` этот ключ нужно убрать/обнулить.
- **`domains: []` в коде означает «диапазон не участвует в сопоставлении»** (`App.cs` делает `continue`), а README
  описывает `[]` как catch-all для всех прочих доменов. Это расхождение документов и кода: прежде чем «чинить»,
  решите, какая сторона правильная, и обновите обе.
- `disabled` (корневой) и `disabled` внутри элемента `configs[]` — разные вещи; корневой читается как `disabled`
  (исторический баг с `disable` исправлен в 4.2.7).
- `maxTtl` ограничивает TTL ответа **и** уезжает в gRPC-агенту; в `nft`-режиме игнорируется (см. инвариант 7).
- Размер диапазона ограничен `int.MaxValue` адресов — это `BaseIpAllocator.MaxRangeSize`, чтобы количество
  адресов аренды всегда помещалось в `int`. Крупнейший CIDR — `/2` для IPv4 и `/98` для IPv6: `/1` и `/97` —
  ровно `2^31` адресов, на один больше лимита; `0.0.0.0/0` и `::/0` отвергаются.
- `ipV6Range: "fc00::/110"` — безопасный пример, а не рабочая схема (про ULA, приоритеты RFC 6724 и выбор
  собственного `/110` из своего `/48` — раздел «Какой брать диапазон» в README).
- `logging: true` включает логирование `nft`-действий через `IDnsServer.WriteLog`; в gRPC-режиме не используется.

## Известные расхождения и долги

- `domains: []` — см. выше (README ↔ `App.cs`).
- `ttl` не применяется в `LocalNftIpMap`.
- `DeleteMapping` не проверяет `ExitCode`: удаление несуществующего элемента — штатная ситуация, поэтому ошибка
  удаления молча игнорируется (stderr при этом вычитывается, чтобы не вешать процесс `nft`).
- `Cache && ответ содержит CNAME` — обходной путь (`Hack for CNAME`) через `DnsServer.DirectQueryAsync` с типом
  `ANY`; он зависит от API DNS Server 15.x и при обновлении хоста его надо перепроверять.

## Частые задачи

- **Поднять версию хоста**: `versions.json` (или `get-libs.ps1 -ReleaseVersion v15.7.0`) → `get-libs.ps1` →
  сборка → новый блок `Unreleased`/релиз в `CHANGELOG.md` → `<Version>` в csproj. Проверьте, что контракты
  `IDnsApplication`/`IDnsPostProcessor` не изменились (`.deps\DnsServer\DnsServerCore.ApplicationCommon\`).
- **Добавить ключ конфига**: прочитать в `App.InitializeAsync` через `GetPropertyValue("<ключ>", <дефолт>)`,
  добавить в `src/FakeIpDnsApp/dnsApp.config`, в таблицу README и в CHANGELOG.
- **Новый способ маппинга**: класс в `Services/` с `IIpMap`, выбор реализации в `InitializeAsync`; не забыть
  `Dispose()` и `ObjectDisposedException`-безопасность при перечитывании конфига.
- **Менять proto**: `src/FakeIpDnsApp/Protos/ipMap.proto` (генерация через `Grpc.Tools`, `GrpcServices="Client"`)
  + синхронизация с gRPC-агентом.

## Чего не делать

- Не копировать `libs\*.dll` в `bin\`, `publish`, `dist\` или в архив; не добавлять хостовые сборки как
  `PackageReference`/`ProjectReference`.
- Не коммитить `libs/`, `.deps/`, `bin/`, `obj/`, `dist/`, `.idea/`.
- Не править ничего внутри `.deps\`.
- Не менять `TargetFramework`/`LangVersion` в `FakeIpDnsApp.csproj` и не возвращать поддержку .NET 8/9.
- Не запускать `nft`-режим на Windows при проверке — только gRPC-агент.
- Не переименовывать `FakeIpDnsApp` (ни сборку, ни namespace) и не возвращать старое имя `FakeIpRouting`.
- Не писать в `README.md`/`CHANGELOG.md` утверждения, которых нет в коде, — про `domains: []` это особенно актуально.

## Коммиты

- Короткий императив с conventional-префиксом: `fix: ...`, `build: ...`, `docs: ...`, `refactor: ...`,
  `chore: ...`, `feat: ...` (ранние коммиты без префикса — не ориентир).
- Изменение версии, контрактов конфига или поведения приложения сопровождается записью в `CHANGELOG.md`
  (`Added` / `Changed` / `Fixed` / `Removed`, русские формулировки).

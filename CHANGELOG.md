# Changelog

Формат — [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версия — семантическая.
Версия намеренно продолжает нумерацию приложения в форке `DnsFakeIpServer` (`4.2.6`), чтобы DNS Server считал
сборки из этого репозитория более новыми, чем уже установленные.

## [4.2.7] - 2026-11-25

### Added

- Отдельный репозиторий с собственной git-историей: 17 коммитов приложения импортированы из `Apps/FakeIpRouting`
  форка Technitium DNS Server (авторы, даты и сообщения сохранены).
- `build/get-libs.ps1` — скачивание и сборка compile-time сборок хоста (`DnsServerCore.ApplicationCommon`,
  `TechnitiumLibrary`, `TechnitiumLibrary.Net`) по тегам из `versions.json`; результат в `libs/`.
- `build/package.ps1` — `dotnet publish` + плоский архив `dist/FakeIpDnsApp-<version>.zip` для установки в
  `Settings → Apps`; сборки хоста и `.pdb` в архив не попадают.
- `Directory.Build.props` (`net10.0`, C# 14, `Nullable`, плоский путь вывода), `versions.json`, `FakeIpDnsApp.sln`,
  `README.md`, `LICENSE` (GPL-3.0-only), этот файл.

### Changed

- Проект переименован в `FakeIpDnsApp`: пространства имён `FakeIpRouting*` → `FakeIpDnsApp*`, сборка
  `FakeIpDnsApp.dll`, класс приложения `App` (как у официальных DNS App), корневое пространство имён в `.csproj`.
- Ссылки на хостовые сборки вместо `ProjectReference`: `<Reference>` + `HintPath` в `libs/` и `Private=false`.
  Решение больше не нужно, исходники Technitium не тянутся.
- `Grpc.Tools` `2.82.0-pre1` → `2.80.0` (стабильная версия, та же линия, что у `Grpc.Net.Client 2.80.0`).
- Поддержка только `net10.0`: старые целевые платформы DNS Server не обслуживаются.

### Fixed

- Корневой параметр `disabled` в `dnsApp.config` не применялся: код читал ключ `disable` и всегда получал `false`.
  Теперь читается `disabled`, приложение действительно можно выключить конфигурацией.

### Removed

- Пустой `README.md` внутри папки приложения (описание переехало в корень репозитория).

## [4.2.6] и раньше

Разработка в папке `Apps/FakeIpRouting` форка Technitium DNS Server: диапазоны CIDR и диапазоны-диапазоны,
несколько диапазонов с собственными лимитами адресов на домен, gRPC-агент маппинга, `nft`-таблицы и maps,
аллокация адресов с освобождением по TTL, `ignoreCase`, `maxTtl`, отключение кэширования ответа.
Полная история — в `git log` (коммиты сохранены при импорте).

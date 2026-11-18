# TrustTunnelManager

Web-панель для мониторинга и администрирования [TrustTunnel](https://github.com/TrustTunnel/TrustTunnel).

## Технологии

- .NET 10 LTS / ASP.NET Core Blazor Web App / Interactive Server
- Clean Architecture: Domain, Application, Infrastructure, Web
- SQLite + ASP.NET Core Identity
- TrustTunnel Prometheus Metrics API
- Tomlyn для безопасной работы с TOML
- QRCoder для локальной генерации QR-кодов
- systemd / journald для управления службой на Linux

## Возможности текущей версии

### Мониторинг

- Проверка доступности endpoint через `/health-check`.
- Чтение Prometheus-метрик через `/metrics`.
- Число активных сессий по протоколам.
- Суммарный входящий/исходящий трафик и TCP/UDP-сокеты.
- Статистика пользователей через `/clients` при включённом `per_client_metrics`.
- Автообновление панели мониторинга.

### Управление пользователями

- Просмотр списка записей из `credentials.toml`.
- Создание, изменение и удаление пользователя.
- Смена пароля и генерация случайного пароля.
- Настройка `max_http2_conns` и `max_http3_conns`.
- Экспорт конфигурации штатным `trusttunnel_endpoint --client_config` в TOML или `tt://` deep link.
- Локальная генерация QR-кода для deep link. Конфигурация не отправляется QR-сервисам третьих сторон.

Пароли существующих пользователей не выводятся в интерфейс и не возвращаются из Application API.

### Правила доступа

- Просмотр и изменение `rules.toml`.
- CIDR, Client Random Prefix, маска и Client Random Auth Key.
- Действия allow/deny.
- Перестановка правил с учётом порядка обработки TrustTunnel.
- Предупреждения о catch-all правилах.

### Служба и журнал

- Статус systemd unit.
- Запуск, остановка, перезапуск и отправка SIGHUP.
- Просмотр последних записей journald.
- Передача команд через `ProcessStartInfo.ArgumentList`, без shell-интерполяции.

### Безопасность и аудит

- ASP.NET Core Identity, SQLite и закрытая регистрация.
- Первый администратор создаётся при первоначальном запуске через переменные окружения.
- Antiforgery для входа/выхода.
- HttpOnly/Secure cookies (Secure в production).
- Журнал действий администратора и результатов операций.
- Резервная копия TOML перед изменением.
- Атомарная замена файла, перезапуск endpoint и попытка rollback при неудачном применении.

## Структура решения

| Проект | Ответственность |
| --- | --- |
| `TrustTunnelManager.Domain` | Модели предметной области |
| `TrustTunnelManager.Application` | Контракты и сценарии приложения |
| `TrustTunnelManager.Infrastructure` | TOML, процессы, systemd, TrustTunnel API |
| `TrustTunnelManager.Web` | Blazor UI, Identity, SQLite и композиция приложения |

## Настройка TrustTunnel

В `vpn.toml` задайте используемые файлы и включите метрики:

```toml
credentials_file = "credentials.toml"
rules_file = "rules.toml"

[metrics]
address = "127.0.0.1:1987"
request_timeout_secs = 3
per_client_metrics = true
```

TrustTunnel предоставляет:

- `GET /health-check` — состояние endpoint;
- `GET /metrics` — метрики Prometheus;
- `GET /clients` — персональная статистика (только при `per_client_metrics = true`).

**Важно:** TrustTunnel Metrics API не имеет собственной авторизации. Не публикуйте порт метрик в интернет; оставляйте его на loopback.

Настройки путей, systemd и Metrics API расположены в `TrustTunnelManager.Web/appsettings.json`. Адреса серверов, используемые при экспорте конфигураций, задаются в `TrustTunnel:ClientConfiguration:Addresses`.

## Запуск для разработки

Требуется .NET 10 SDK.

```bash
dotnet restore TrustTunnelManager.slnx
dotnet run --project TrustTunnelManager.Web
```

На локальной машине первый администратор задаётся переменными окружения:

```bash
TrustTunnelManager__BootstrapAdmin__Username=admin
TrustTunnelManager__BootstrapAdmin__Password='REPLACE_WITH_A_LONG_RANDOM_PASSWORD'
```

Публичной регистрации нет. После первого создания администратора bootstrap-переменные нужно удалить.

## Установка на Linux

См. подробную [инструкцию по установке и настройке systemd/sudoers](docs/deployment/linux.md).

## Многосерверная архитектура

Целевая модель — симметричная сеть TrustTunnelManager: каждый узел владеет локальным endpoint, а доверенные узлы смогут запрашивать мониторинг и направлять операции владельцу. Архитектура и требования безопасности описаны в [docs/architecture/multi-node.md](docs/architecture/multi-node.md).

**Важно:** на текущем этапе реализовано локальное управление endpoint. Межсерверное сопряжение, mTLS peer API и удалённое выполнение команд из другой панели ещё не реализованы.

## Ограничения

- Изменения пользователей и правил требуют перезапуска TrustTunnel; активные подключения будут прерваны.
- SIGHUP обновляет TLS-хосты и настройки подписки, но не credentials/rules.
- Встроенные счётчики трафика TrustTunnel накопительные с момента запуска процесса и сбрасываются после перезапуска endpoint.
- Историческое хранение и графики скорости ещё не реализованы.


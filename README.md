# TrustTunnelManager

Веб-панель для мониторинга и администрирования [TrustTunnel](https://github.com/TrustTunnel/TrustTunnel).

## Технологии

- .NET 10 LTS
- ASP.NET Core Blazor Web App
- Interactive Server
- Clean Architecture
- TrustTunnel Prometheus-compatible Metrics API

## Структура решения

| Проект | Ответственность |
| --- | --- |
| `TrustTunnelManager.Domain` | Модели предметной области |
| `TrustTunnelManager.Application` | Контракты и сценарии приложения |
| `TrustTunnelManager.Infrastructure` | Интеграция с TrustTunnel и инфраструктурой |
| `TrustTunnelManager.Web` | Blazor UI и композиция приложения |

## Мониторинг TrustTunnel

На сервере TrustTunnel включите метрики в `vpn.toml`:

```toml
[metrics]
address = "127.0.0.1:1987"
request_timeout_secs = 3
per_client_metrics = true
```

TrustTunnel предоставляет:

- `GET /health-check` — состояние endpoint;
- `GET /metrics` — метрики Prometheus;
- `GET /clients` — статистика по пользователям (только при `per_client_metrics = true`).

**Безопасность:** интерфейс метрик TrustTunnel не имеет собственной авторизации. Не публикуйте порт метрик в интернет. При размещении Manager и endpoint на одной машине используйте loopback-адрес `127.0.0.1`. Для раздельного размещения используйте защищённый приватный канал и firewall.

Адрес Metrics API настраивается в `TrustTunnelManager.Web/appsettings.json`:

```json
{
  "TrustTunnel": {
    "Metrics": {
      "BaseAddress": "http://127.0.0.1:1987/",
      "TimeoutSeconds": 3
    }
  }
}
```

Счётчики трафика TrustTunnel накопительные и сбрасываются при перезапуске endpoint. Историческое хранение, графики скорости и аудит будут добавлены отдельным этапом.

## Запуск

Требуется .NET 10 SDK.

```bash
dotnet restore TrustTunnelManager.slnx
dotnet run --project TrustTunnelManager.Web
```

Для локальной разработки используйте HTTPS-профиль из `Properties/launchSettings.json`.

## Текущий этап

- [x] Blazor Web App на .NET 10 с Interactive Server
- [x] Разделение Domain / Application / Infrastructure / Web
- [x] Чтение health-check и Prometheus metrics
- [x] Отображение активных сессий, TCP/UDP сокетов и трафика
- [x] Отображение статистики пользователей при включённом per-client metrics
- [ ] Авторизация администратора
- [ ] Управление пользователями TrustTunnel
- [ ] Управление правилами фильтрации
- [ ] Управление systemd и просмотр journald
- [ ] Историческое хранение метрик и аудит

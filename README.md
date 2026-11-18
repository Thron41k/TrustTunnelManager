# TrustTunnelManager

Распределённая Web-панель управления **TrustTunnel Client** на нескольких Ubuntu-серверах.

## Целевая схема

На каждом сервере одновременно работают:
- уже настроенный `trusttunnel_client` как systemd-служба;
- собственный экземпляр TrustTunnelManager.

Менеджеры равноправны. Из Web UI любого узла можно получить состояние всех сопряжённых узлов и управлять локальной или удалённой службой. Каждый менеджер является единственным владельцем локального клиента и его конфигурации.

## Возможности

- Состояние службы systemd, PID, потребление памяти, CPU time.
- Опциональные RX/TX счётчики интерфейса из sysfs.
- Запуск, остановка и перезапуск выбранного клиента.
- Просмотр journald.
- Просмотр и атомарное применение клиентской конфигурации с revision/hash, backup и rollback.
- Сопряжение менеджеров одноразовым токеном на 5 минут.
- mTLS с отдельным сертификатом узла и проверкой SHA-256 fingerprint.
- Отзыв доверия к peer, локальный JSONL аудит.
- Cookie-аутентификация UI администратора; пароли задаются через environment.

## Безопасность

Нет API произвольного shell-выполнения. Peer API принимает только ограниченный набор операций и требует HTTPS + зарегистрированный клиентский сертификат. Pairing token одноразовый и используется только для первичной привязки сертификатов. Установите firewall между узлами и не публикуйте внутренний API в интернет.

## Запуск и публикация

Требуется .NET 10 SDK:

```bash
dotnet restore TrustTunnelManager.slnx
dotnet build TrustTunnelManager.slnx -c Release
dotnet run --project TrustTunnelManager.Web
```

Для запуска обязательно задайте `Admin__Username`, `Admin__Password`, `Mesh__IdentityCertificatePath`, `Mesh__IdentityCertificatePassword` и `Mesh__AdvertisedBaseUrl`.

Полная инструкция для Ubuntu: [docs/deployment/ubuntu.md](docs/deployment/ubuntu.md). Архитектура и границы доверия: [docs/architecture/multi-node.md](docs/architecture/multi-node.md).

## Ограничения первой версии

- Peer-каталог содержит прямые доверенные связи; для нескольких узлов настройте mesh-соединение между каждой парой. Транзитивная маршрутизация и репликация каталога отключены.
- Историческая репликация метрик не реализована.
- Конфигурация сохраняется как текст и не проходит версионно-зависимый TOML semantic validation; перед применением выполняется проверка revision, резервирование и попытка rollback при ошибке restart.
- Для точного сетевого трафика укажите имя VPN-интерфейса в `TrustTunnelClient__NetworkInterface`.

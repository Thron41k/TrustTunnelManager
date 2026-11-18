# Установка TrustTunnelManager на Linux

Инструкция рассчитана на размещение TrustTunnelManager на той же машине, где запущен управляемый TrustTunnel endpoint.

## 1. Требования

- Linux с systemd.
- Установленный TrustTunnel endpoint и его рабочий systemd unit (по умолчанию `trusttunnel.service`).
- .NET 10 ASP.NET Core Runtime.
- Доступ к файлам `vpn.toml`, `hosts.toml`, `credentials.toml` и `rules.toml`.
- Пользователь службы TrustTunnelManager с отдельной учётной записью без root.

## 2. Настройка TrustTunnel

В `vpn.toml` укажите используемые файлы и включите метрики:

```toml
credentials_file = "credentials.toml"
rules_file = "rules.toml"

[metrics]
address = "127.0.0.1:1987"
request_timeout_secs = 3
per_client_metrics = true
```

Пути к файлам могут быть абсолютными или относительными к каталогу `vpn.toml`.

`rules_file` необязателен для TrustTunnel. Для управления правилами из Manager он должен быть задан. Создайте `rules.toml`, например:

```toml
# Пустой список означает, что по умолчанию разрешены все подключения.
```

Перезапустите TrustTunnel после изменения `vpn.toml`. Метрики должны слушать только loopback: endpoint мониторинга не имеет собственной авторизации.

## 3. Подготовка системных пользователей и доступа к файлам

Создайте отдельного пользователя приложения и группу для конфигурации. Добавьте в эту группу пользователя TrustTunnel, если служба работает от отдельной учётной записи:

```bash
sudo useradd --system --create-home --home-dir /opt/trusttunnelmanager \
  --shell /usr/sbin/nologin trusttunnelmanager

sudo groupadd --system trusttunnel-config
sudo usermod -aG trusttunnel-config trusttunnelmanager
# Выполните следующую команду, если TrustTunnel работает от пользователя trusttunnel:
sudo usermod -aG trusttunnel-config trusttunnel

sudo chgrp trusttunnel-config /opt/trusttunnel
sudo chmod 2770 /opt/trusttunnel

sudo chgrp trusttunnel-config \
  /opt/trusttunnel/vpn.toml \
  /opt/trusttunnel/hosts.toml \
  /opt/trusttunnel/credentials.toml \
  /opt/trusttunnel/rules.toml

sudo chmod 0660 \
  /opt/trusttunnel/vpn.toml \
  /opt/trusttunnel/hosts.toml \
  /opt/trusttunnel/credentials.toml \
  /opt/trusttunnel/rules.toml
```

Убедитесь, что пользователь, под которым запущен TrustTunnel, по-прежнему может читать свои конфигурационные файлы. Если endpoint работает под другим пользователем/группой, добавьте его в `trusttunnel-config` либо назначьте совместимую группу.

Каталог должен быть доступен Manager на запись: приложение создаёт резервные копии и временный файл, затем атомарно заменяет конфигурацию. Резервные копии содержат пароли и ключи, поэтому права на каталог и резервные файлы должны оставаться ограниченными.

Добавьте пользователя Manager в группу чтения журналов:

```bash
sudo usermod -aG systemd-journal trusttunnelmanager
```

После изменения групп перезапустите службу Manager.

## 4. Ограниченные права systemd

Manager не запускается от root. Для команд управления конкретной службой используется `sudo -n` с отдельными точными разрешениями.

Создайте файл `/etc/sudoers.d/trusttunnelmanager` через `visudo`:

```sudoers
Cmnd_Alias TRUSTTUNNEL_MANAGER_COMMANDS = \
    /usr/bin/systemctl show trusttunnel.service --property=LoadState --property=ActiveState --property=SubState --property=UnitFileState --property=MainPID --no-pager, \
    /usr/bin/systemctl start trusttunnel.service, \
    /usr/bin/systemctl stop trusttunnel.service, \
    /usr/bin/systemctl restart trusttunnel.service, \
    /usr/bin/systemctl kill --signal=HUP --kill-who=main trusttunnel.service

trusttunnelmanager ALL=(root) NOPASSWD: TRUSTTUNNEL_MANAGER_COMMANDS
```

Проверьте синтаксис:

```bash
sudo visudo -cf /etc/sudoers.d/trusttunnelmanager
```

Если имя службы отличается от `trusttunnel.service`, измените одновременно `UnitName` в настройках Manager и все соответствующие строки sudoers.

Чтение journald по умолчанию выполняется без sudo: достаточно членства пользователя в группе `systemd-journal`. Не включайте `UseSudoForJournalctl`, если в этом нет необходимости.

## 5. Публикация

Соберите приложение на машине разработки или CI:

```bash
dotnet restore TrustTunnelManager.slnx
dotnet publish TrustTunnelManager.Web/TrustTunnelManager.Web.csproj \
  -c Release -o ./publish
```

Скопируйте содержимое каталога publish в `/opt/trusttunnelmanager`:

```bash
sudo mkdir -p /opt/trusttunnelmanager/App_Data
sudo chown -R trusttunnelmanager:trusttunnelmanager /opt/trusttunnelmanager
```

В `appsettings.json` проверьте пути к трём файлам TrustTunnel и бинарнику:

```json
{
  "TrustTunnel": {
    "Paths": {
      "SettingsFile": "/opt/trusttunnel/vpn.toml",
      "TlsHostsFile": "/opt/trusttunnel/hosts.toml",
      "ExecutablePath": "/opt/trusttunnel/trusttunnel_endpoint"
    },
    "Service": {
      "UnitName": "trusttunnel.service",
      "UseSudo": true,
      "UseSudoForJournalctl": false
    },
    "ClientConfiguration": {
      "Addresses": [ "vpn.example.com:443" ],
      "DisplayName": "My TrustTunnel",
      "DnsUpstreams": [ "tls://1.1.1.1" ]
    },
    "Metrics": {
      "BaseAddress": "http://127.0.0.1:1987/",
      "TimeoutSeconds": 3
    }
  }
}
```

Задайте реальные адреса endpoint в `ClientConfiguration:Addresses`. Они используются при генерации конфигураций, когда адрес не указан в форме пользователя.

## 6. Первый администратор

Публичной регистрации нет. Первый администратор создаётся один раз при пустой базе данных через переменные окружения.

Создайте файл `/etc/trusttunnelmanager/bootstrap.env`:

```ini
TrustTunnelManager__BootstrapAdmin__Username=admin
TrustTunnelManager__BootstrapAdmin__Password=REPLACE_WITH_A_LONG_RANDOM_PASSWORD
```

Ограничьте доступ:

```bash
sudo chown root:root /etc/trusttunnelmanager/bootstrap.env
sudo chmod 0600 /etc/trusttunnelmanager/bootstrap.env
```

Временно подключите этот файл через `EnvironmentFile` в systemd unit ниже. При первом старте Manager создаст администратора в SQLite. После успешного создания и проверки входа удалите `EnvironmentFile` из unit и сам секретный файл, затем перезапустите Manager. Учётная запись останется в базе.

Если переменные bootstrap не заданы, база будет создана, но пользователь не появится. Установите переменные и перезапустите приложение, пока в базе нет пользователей.

## 7. systemd unit для Manager

Создайте `/etc/systemd/system/trusttunnelmanager.service`:

```ini
[Unit]
Description=TrustTunnel Manager Web Application
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=trusttunnelmanager
Group=trusttunnelmanager
SupplementaryGroups=trusttunnel-config systemd-journal
WorkingDirectory=/opt/trusttunnelmanager
ExecStart=/usr/bin/dotnet /opt/trusttunnelmanager/TrustTunnelManager.Web.dll
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
EnvironmentFile=-/etc/trusttunnelmanager/bootstrap.env
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/opt/trusttunnel /opt/trusttunnelmanager/App_Data

[Install]
WantedBy=multi-user.target
```

Здесь `ReadWritePaths` разрешает Manager менять только каталог TrustTunnel с конфигурацией и его SQLite-каталог. Учётная запись остаётся непривилегированной; привилегированные команды выполняются через точечные правила sudoers.

Включите службу:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now trusttunnelmanager
sudo journalctl -u trusttunnelmanager -f
```

## 8. Доступ через HTTPS

Не публикуйте порт приложения `5080` напрямую в интернет. Привяжите его к loopback (как в примере выше) и разместите за reverse proxy с TLS либо настройте HTTPS непосредственно в Kestrel.

При использовании локального Nginx добавьте в location:

```nginx
proxy_pass http://127.0.0.1:5080;
proxy_http_version 1.1;
proxy_set_header Host $host;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection "upgrade";
```

В `ForwardedHeaders:KnownProxies` перечислите адреса только доверенных reverse proxy. По умолчанию доверены loopback-адреса. Не принимайте forwarded-заголовки от произвольных источников.

В production cookie авторизации помечена `Secure`, поэтому панель должна открываться только по HTTPS.

## 9. Резервное копирование

Manager сохраняет копию изменяемого TOML-файла перед записью с суффиксом времени `.bak`. Дополнительно регулярно сохраняйте:

- `/opt/trusttunnel/vpn.toml`;
- `hosts.toml`, `credentials.toml`, `rules.toml` и резервные копии;
- `/opt/trusttunnelmanager/App_Data/trusttunnelmanager.db`.

В базе хранятся администраторы и аудит. Резервные копии конфигураций и базы содержат чувствительные сведения.

## 10. Ограничения текущего этапа

- Изменение пользователей и правил требует перезапуска TrustTunnel и прерывает активные VPN-подключения.
- `SIGHUP` обновляет TLS-хосты и настройки подписки, но не перечитывает credentials/rules.
- Метрики TrustTunnel доступны без отдельной авторизации и должны оставаться на loopback.
- Межсерверное сопряжение и удалённое выполнение операций из другой панели пока описаны в [multi-node.md](../architecture/multi-node.md), но ещё не реализованы.
- Встроенные счётчики TrustTunnel сбрасываются при перезапуске endpoint; долговременная история и расчёт скорости будут отдельным этапом.

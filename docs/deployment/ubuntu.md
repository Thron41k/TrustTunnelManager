# Ubuntu deployment

Выполните процедуру независимо на обоих Ubuntu-серверах. На каждом из них уже должен быть настроен свой `trusttunnel_client` как systemd-служба.

## Подготовка

Установите .NET 10 ASP.NET Core Runtime и создайте отдельную учётную запись Manager:

```bash
sudo useradd --system --home /var/lib/trusttunnelmanager --shell /usr/sbin/nologin trusttunnelmanager
sudo install -d -o trusttunnelmanager -g trusttunnelmanager -m 0750 /var/lib/trusttunnelmanager
sudo install -d -o root -g trusttunnelmanager -m 0750 /etc/trusttunnelmanager
systemctl list-units --type=service | grep -i trusttunnel
systemctl cat trusttunnel-client.service
```

Проверьте фактическое имя службы и путь конфигурации клиента. Если они отличаются от примеров, укажите свои значения в переменных окружения. Не подменяйте уже работающий `trusttunnel_client` новым сервером или endpoint: Manager управляет именно локальной клиентской службой.

Установите `curl`, если он ещё не установлен: `sudo apt-get install -y curl`. Встроенный watchdog Manager использует его для HTTPS-проверки через VPN-интерфейс.

## Сертификат каждого узла

Каждому Manager нужен отдельный TLS-сертификат с закрытым ключом. Один и тот же сертификат применяется Kestrel для HTTPS и отправляется как клиентский сертификат mTLS. Для production используйте сертификат доверенного CA с SAN, совпадающим с DNS-именем узла. Для уже выпущенного сертификата Let's Encrypt:

```bash
sudo openssl pkcs12 -export -out /etc/trusttunnelmanager/node-identity.pfx -inkey /etc/letsencrypt/live/client-1.example.net/privkey.pem -in /etc/letsencrypt/live/client-1.example.net/fullchain.pem
sudo chown root:trusttunnelmanager /etc/trusttunnelmanager/node-identity.pfx
sudo chmod 0640 /etc/trusttunnelmanager/node-identity.pfx
openssl pkcs12 -in /etc/trusttunnelmanager/node-identity.pfx -clcerts -nokeys | openssl x509 -noout -fingerprint -sha256
```

Замените домен на свой. На втором сервере используйте его собственный сертификат. Не копируйте PFX между узлами. Пароль PFX защитите и задайте через environment. Сертификат собственной CA также подходит, если CA доверена браузерам администраторов.

## Environment

Создайте файл `/etc/trusttunnelmanager/manager.env`:

```ini
Admin__Username=admin
Admin__Password=REPLACE_WITH_RANDOM_PASSWORD_AT_LEAST_16_CHARS
Mesh__NodeName=ubuntu-client-1
Mesh__AdvertisedBaseUrl=https://client-1.example.net:5080
Mesh__IdentityCertificatePath=/etc/trusttunnelmanager/node-identity.pfx
Mesh__IdentityCertificatePassword=REPLACE_WITH_PFX_PASSWORD
Mesh__StateDirectory=/var/lib/trusttunnelmanager
TrustTunnelClient__ServiceUnit=trusttunnel.service
TrustTunnelClient__ConfigurationFile=/opt/trusttunnel_client/trusttunnel_client.toml
TrustTunnelClient__NetworkInterface=tun0
TrustTunnelClient__UseSudo=true
TrustTunnelClient__UseSudoForJournalctl=true
Watchdog__Enabled=true
Watchdog__Interface=tun0
Watchdog__ProbeUrl=https://www.cloudflare.com/cdn-cgi/trace
Watchdog__UseSudo=true
Watchdog__SudoPath=/usr/bin/sudo
```

На втором сервере укажите другое имя узла, свой HTTPS URL, свой сертификат и его пароль. AdvertisedBaseUrl должен быть достижим со второго узла и совпадать с SAN сертификата. Он должен содержать только origin, например `https://node-b.example.net:5080`, без пути и query.

```bash
sudo chown root:trusttunnelmanager /etc/trusttunnelmanager/manager.env
sudo chmod 0640 /etc/trusttunnelmanager/manager.env
```

## Права на конфигурацию клиента

Менеджер создаёт временный файл рядом с конфигурацией и заменяет исходный атомарным rename. Поэтому он должен иметь права записи в каталог, а сам `trusttunnel_client` должен иметь права чтения нового файла. Рекомендуется общая группа:

```bash
sudo groupadd --system trusttunnel-config
sudo usermod -aG trusttunnel-config trusttunnelmanager
# При необходимости добавьте пользователя службы trusttunnel_client в эту группу.
sudo chown root:trusttunnel-config /etc/trusttunnel
sudo chmod 2770 /etc/trusttunnel
sudo chown root:trusttunnel-config /etc/trusttunnel/client.toml
sudo chmod 0660 /etc/trusttunnel/client.toml
```

## Ограниченный sudoers

Создайте `/etc/sudoers.d/trusttunnelmanager` через `visudo`. Ниже показаны правила для существующей службы `trusttunnel.service` и проверки через `tun0`. При других значениях `Watchdog__Interface`, URL проверки или таймаутов измените строку `TTM_PROBE`, чтобы она точно соответствовала аргументам команды в конфигурации:

```sudoers
Cmnd_Alias TTM_SYSTEMD = /usr/bin/systemctl show trusttunnel.service --no-pager *, /usr/bin/systemctl start trusttunnel.service, /usr/bin/systemctl stop trusttunnel.service, /usr/bin/systemctl restart trusttunnel.service
Cmnd_Alias TTM_JOURNAL = /usr/bin/journalctl -u trusttunnel.service -n * --no-pager -o short-iso-precise
Cmnd_Alias TTM_PROBE = /usr/bin/curl --interface tun0 --connect-timeout 5 --max-time 12 --silent --show-error --fail --output /dev/null --url https://www.cloudflare.com/cdn-cgi/trace
trusttunnelmanager ALL=(root) NOPASSWD: TTM_SYSTEMD, TTM_JOURNAL, TTM_PROBE
```

Wildcard в команде `systemctl show` разрешает только чтение свойств указанного unit; он нужен, чтобы sudoers корректно обработал список свойств, разделённых запятыми. Запуск/остановка/перезапуск и просмотр journald ограничены указанным unit. Команда `TTM_PROBE` разрешает только заданный HTTPS-запрос через `tun0`; watchdog вызывает sudo в non-interactive режиме. Проверьте фактические пути systemctl/journalctl/curl и затем выполните `sudo visudo -cf /etc/sudoers.d/trusttunnelmanager`.

Проверьте разрешение команды от имени Manager до запуска службы:

```bash
sudo -u trusttunnelmanager sudo -n -- /usr/bin/curl --interface tun0 --connect-timeout 5 --max-time 12 --silent --show-error --fail --output /dev/null --url https://www.cloudflare.com/cdn-cgi/trace
echo $?
```

Ожидаемый код возврата — `0`. Не выдавайте Manager произвольный sudo.

## Установка Manager

```bash
dotnet publish TrustTunnelManager.Web/TrustTunnelManager.Web.csproj -c Release -o /opt/trusttunnelmanager
sudo chown -R root:root /opt/trusttunnelmanager
```

Создайте `/etc/systemd/system/trusttunnelmanager.service`:

```ini
[Unit]
Description=TrustTunnel Manager
After=network-online.target
Wants=network-online.target

[Service]
User=trusttunnelmanager
Group=trusttunnelmanager
SupplementaryGroups=trusttunnel-config
WorkingDirectory=/opt/trusttunnelmanager
EnvironmentFile=/etc/trusttunnelmanager/manager.env
Environment=ASPNETCORE_URLS=https://0.0.0.0:5080
UMask=0077
ExecStart=/usr/bin/dotnet /opt/trusttunnelmanager/TrustTunnelManager.Web.dll
Restart=always
RestartSec=5
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/trusttunnelmanager /etc/trusttunnel

[Install]
WantedBy=multi-user.target
```

Здесь намеренно не включён `NoNewPrivileges=true`: он блокирует повышение привилегий через setuid sudo и не позволит выполнить перечисленные выше узкие sudoers-команды. Безопасность управления обеспечивается whitelist-ом операций в приложении и ограниченными командами sudoers.

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now trusttunnelmanager
sudo systemctl status trusttunnelmanager
```

Откройте TCP 5080 только между доверенными серверами и административными сетями. Не завершайте TLS перед peer API: приложение проверяет клиентский сертификат непосредственно в TLS connection. Используйте прямой HTTPS или TCP passthrough. Не публикуйте порт peer API в общедоступный Интернет.

## Встроенный VPN watchdog

Manager автоматически проверяет доступность HTTPS через указанный интерфейс (по умолчанию `tun0`). Поведение соответствует прежнему скрипту watchdog:

- первая проверка через 2 минуты после запуска Manager;
- следующие проверки каждую минуту;
- после трёх последовательных неудач Manager перезапускает заданный `TrustTunnelClient:ServiceUnit`;
- автоматические перезапуски ограничены интервалом не чаще одного раза в 5 минут;
- успешная проверка сбрасывает счётчик ошибок.

Состояние проверки, ошибки и время последнего автоматического перезапуска отображаются в таблице узлов. Для просмотра удалённого watchdog оба узла должны работать на версии Manager с поддержкой watchdog.

**Не удаляйте старый watchdog до успешной проверки нового.** После запуска Manager дождитесь в интерфейсе статуса «VPN доступен». Затем удалите старую systemd-службу и таймер:

```bash
sudo systemctl disable --now trusttunnel-watchdog.timer
sudo systemctl stop trusttunnel-watchdog.service 2>/dev/null || true
sudo rm -f /etc/systemd/system/trusttunnel-watchdog.service
sudo rm -f /etc/systemd/system/trusttunnel-watchdog.timer
sudo rm -f /usr/local/sbin/trusttunnel-watchdog.sh
sudo rm -rf /run/trusttunnel-watchdog
sudo systemctl daemon-reload
sudo systemctl reset-failed trusttunnel-watchdog.service
```

Это не останавливает и не изменяет `trusttunnel.service`.

## Сопряжение

1. Откройте Manager на целевом узле B и войдите под администратором.
2. В разделе «Узлы кластера» нажмите «Создать одноразовое приглашение».
3. Передайте администратору узла A HTTPS URL узла B, SHA-256 fingerprint сертификата B и токен. Токен действует пять минут и может быть использован только один раз.
4. На узле A введите URL узла B, его fingerprint и токен, затем нажмите «Сопрячь узлы».
5. После успешного сопряжения оба Manager сохраняют идентификатор и сертификат второго узла. Проверьте доступность обоих узлов в обоих интерфейсах.

Для кластера более чем из двух узлов создайте прямое сопряжение каждой пары. Транзитивная маршрутизация в этой версии не включена. При отзыве доверия удалите peer на обеих сторонах: отзыв на одном узле немедленно блокирует вызовы к нему, но сам по себе не меняет реестр второго узла.

## Диагностика

```bash
sudo journalctl -u trusttunnelmanager -f
sudo journalctl -u trusttunnel.service -n 100 --no-pager
sudo -u trusttunnelmanager sudo -n /usr/bin/systemctl show trusttunnel.service --no-pager
sudo visudo -cf /etc/sudoers.d/trusttunnelmanager
```

Проверьте DNS и маршрут, firewall, AdvertisedBaseUrl, SAN и SHA-256 fingerprint сертификата, синхронизацию времени, срок приглашения, имя systemd unit, путь конфигурации, файловые права и sudoers.

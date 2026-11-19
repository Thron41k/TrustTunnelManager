# TrustTunnel Manager — Home Assistant integration

Custom integration for monitoring and restarting the TrustTunnel Client service on one or more TrustTunnel Manager nodes. Version 1.2.2. Tested for the Home Assistant 2025.11 API shape.

## Features

Each configured Manager becomes one HA device with:

- Manager API connectivity;
- TrustTunnel Client running state;
- embedded VPN watchdog health;
- service state, PID, memory and CPU time;
- RX/TX counters for the configured VPN interface;
- watchdog state, consecutive failures, last check and restart details;
- optional **Перезапустить TrustTunnel Client** button.

The integration polls status every 30 seconds. The restart button is created only when a separate management token is configured for that entry. Existing entries can be configured without deleting them: open the integration's **Configure** dialog and set the optional restart control token. The entry reloads and creates the button.

## Security model

Two independent tokens are required on each Manager:

- `Monitoring__ApiToken` — read-only `GET /api/v1/monitoring/status`;
- `Management__ApiToken` — only `POST /api/v1/management/restart`.

The management route has no action argument and always restarts the Manager's configured TrustTunnel Client unit. It does not expose start, stop, configuration editing or arbitrary shell commands. Manager also rate-limits restart requests.

The integration verifies the Manager's TLS certificate against its SHA-256 fingerprint using `aiohttp.Fingerprint`. TLS verification is not disabled. Do not reuse Manager private keys in Home Assistant.

## Installation

1. Copy `custom_components/trusttunnel_manager` into `<HA config>/custom_components/`.
2. Restart Home Assistant.
3. Open **Settings → Devices & services → Add integration** and select **TrustTunnel Manager**.
4. Add each Manager as a separate entry.

For each node enter:

- HTTPS address, for example `https://192.168.1.176:5080`;
- that node's `Monitoring__ApiToken`;
- that node's SHA-256 TLS certificate fingerprint (with or without colons);
- that node's `Management__ApiToken` in the optional restart field. Leave the last field empty to configure read-only monitoring without a restart button.

Configure two entries:

- `https://192.168.1.176:5080` (`thronserver`);
- `https://192.168.20.13:5080` (`vpn`).

Retrieve a token on its own server with `sudo cat /root/trusttunnelmanager-ha-token` or `sudo cat /root/trusttunnelmanager-ha-control-token`. Never paste tokens into public chats or dashboards.

## Configure an existing entry

For each already configured Manager: open **Settings → Devices & services**, open the TrustTunnel Manager integration, choose **Configure**, and enter that server's separate `Management__ApiToken`. Save; the integration reloads and adds its restart button. No need to delete and re-add the integration. The option is stored in the HA config entry (not in the dashboard).

## Lovelace button

After configuring a management token, a button entity named **Перезапустить TrustTunnel Client** is created for that Manager. Open the Manager device and add the button entity to a dashboard using a standard Button card. The button also works from the device's entity list; it is not automatically added to the dashboard.

# Home Assistant monitoring API

TrustTunnelManager exposes an optional, read-only endpoint for Home Assistant:

`GET /api/v1/monitoring/status`

The endpoint returns only the local node identity, the existing TrustTunnel Client systemd snapshot, and the embedded VPN watchdog snapshot. It does not expose peer operations, logs, client configuration, or service-control actions. The watchdog state is returned as a stable string (for example, `Healthy`, `Degraded`, or `RestartFailed`).

## Enable on each Manager

Generate a unique token on each server:

```bash
openssl rand -hex 32
```

Add it to the Manager environment file (`/etc/trusttunnelmanager/manager.env`):

```ini
Monitoring__ApiToken=64-character-random-token
```

Keep the file root-owned and readable only by root and the Manager service account (mode `0640`). Use a different token on each server, then restart:

```bash
sudo systemctl restart trusttunnelmanager.service
sudo systemctl is-active trusttunnelmanager.service
```

A missing token disables the endpoint (HTTP 404). An incorrect token returns HTTP 401. Requests are rate-limited to 30 per minute per source IP and responses are marked `no-store`.

## Test

```bash
curl --fail --silent --show-error \
  --cacert /path/to/trusted-ca.pem \
  -H 'Authorization: Bearer YOUR_TOKEN' \
  https://MANAGER_IP:5080/api/v1/monitoring/status
```

For the existing self-signed certificates, the Home Assistant integration uses SHA-256 certificate pinning. Do not disable TLS verification or reuse a Manager private key in Home Assistant.

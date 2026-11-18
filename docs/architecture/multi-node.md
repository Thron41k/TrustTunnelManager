# Distributed TrustTunnel Client Manager

## Deployment model

Every Ubuntu host runs both:
- the existing `trusttunnel_client` systemd unit;
- one TrustTunnelManager instance that owns and controls only that local client.

There is no master node. The web UI talks only to its local Manager. A Manager routes remote reads and commands to the direct peer that owns the target service. Peer-to-peer links use HTTPS, a client certificate, and SHA-256 certificate pinning in both directions.

## Pairing

1. Configure a valid HTTPS certificate (PFX) and unique advertised HTTPS URL on each Manager.
2. Open **Узлы и управление клиентами** on the receiving Manager and create a one-time invitation. It expires after five minutes.
3. On the other Manager, enter the invitation URL, token and the receiving Manager certificate SHA-256 fingerprint.
4. The joining Manager sends its own certificate and NodeId. The receiver validates the single-use token and the presented certificate fingerprint, stores the peer, and returns its identity. The joining Manager pins and stores the returned peer.
5. The connection is now bidirectional. Revoking a peer removes its fingerprint from the local trust store. Perform revocation on both nodes to remove trust in both directions.

The pairing token is a bootstrap credential, not an administrator password. It is stored hashed and consumed once.

## Security boundaries

- Peer API endpoints require HTTPS and a presented client certificate whose SHA-256 fingerprint is registered.
- Outbound calls present the local certificate and validate the server certificate against the stored fingerprint.
- Pairing is the only endpoint that accepts a not-yet-pinned certificate; it requires the one-use invitation and checks that the certificate fingerprint equals the joining NodeInfo fingerprint.
- The browser never connects directly to a peer API.
- The API exposes a fixed set of status, service-control, log and configuration operations. There is no generic shell execution endpoint.
- systemctl/journalctl are invoked with argument lists (no shell). Sudoers must allow only the configured service unit and required read operations.
- Each local web UI requires an administrator password from the environment. Never expose it without HTTPS.
- Keep the peer HTTPS listener on a private network/VPN or restrict it with a firewall.

## Ownership and consistency

Only the Manager running on a host may read/write its local client configuration and invoke its systemd unit. Remote changes include an expected SHA-256 revision. A stale revision is rejected. Configuration writes create a timestamped backup, use a temporary file and atomic rename, then restart the local service. A failed restart restores the backup and attempts to restart the previous configuration.

If a peer is unavailable it is marked offline/stale and operations fail immediately; no success is fabricated and no command is queued.

## Monitoring

The manager displays service state, PID, memory, CPU time, and optional RX/TX byte counters for the configured Linux network interface (read from sysfs). TrustTunnel Client does not expose the TrustTunnel Endpoint Metrics API; the manager therefore does not query the endpoint's unauthenticated metrics listener.

The initial implementation stores audit records locally as JSONL. Each peer records the source node, target node, actor, action, result and UTC timestamp. Audit replication/history federation is not implemented in this first release.

## Limits

- Pairing creates a direct bidirectional trust link. For more than two nodes, pair each node with every other node; transitive routing/catalog propagation is not enabled.
- Certificate renewal requires updating the pinned fingerprint on the peer (re-pair after revoking the old entry).
- Configuration syntax is not parsed or validated against a particular trusttunnel_client version; the manager applies the supplied file as-is after revision checking. Keep a backup before editing.

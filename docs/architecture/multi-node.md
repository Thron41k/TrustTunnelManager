# Multi-node architecture

## Requirement

TrustTunnelManager must support a group of machines running TrustTunnel endpoints.
Every machine runs its own TrustTunnelManager instance. An administrator can open
the UI on any machine and monitor and manage every trusted node in the group.

The system is symmetric: there is no mandatory permanent primary manager.

## Terminology

- **Node** — one machine participating in the group.
- **Local endpoint** — the TrustTunnel endpoint process controlled by the Manager
  installed on the same machine.
- **Peer** — another trusted node and its internal management API.
- **Cluster** — the set of mutually trusted nodes.

## Topology

Each machine runs:

1. TrustTunnel endpoint.
2. TrustTunnelManager Web application.
3. A local management core that owns access to local configuration files,
   local systemd service and local journald.
4. An internal node API used only for Manager-to-Manager communication.

The browser communicates only with the local Manager. The local Manager reads
its own endpoint directly and sends remote operations to the owning peer's
internal node API.

```text
Browser on Node A                  Browser on Node B
       |                                  |
       v                                  v
+----------------------+         +----------------------+
| TrustTunnelManager A |<------->| TrustTunnelManager B |
| UI + local core      |  mTLS   | UI + local core      |
+----------------------+         +----------------------+
       |                                  |
       v                                  v
 TrustTunnel endpoint A            TrustTunnel endpoint B
       |                                  |
    Clients A                          Clients B
```

The diagram extends horizontally to any number of nodes.

## Ownership and consistency

A node is the single authoritative owner of its local TrustTunnel configuration
and service lifecycle.

- A command targeting Node A is executed only by Node A's local management core.
- A peer must never edit another node's TOML files over a network share.
- Remote commands are routed to the owning node's internal API.
- Configuration changes use validation, backups, atomic file replacement and
  an explicit apply/restart result.
- Each configuration has a revision or content hash. A write must include the
  expected revision to prevent lost updates.
- If a peer is offline, its data is marked stale/unavailable and commands are
  not queued or reported as successful unless durable command queuing is
  explicitly implemented later.

This avoids competing writers and split-brain configuration state.

## Peer enrollment and transport security

Peer communication is a privileged management channel, not a public endpoint.

Required design:

- Bind the internal API to a private interface, VPN interface, or loopback behind
  an authenticated reverse proxy. Never expose it unauthenticated to the public
  Internet.
- Use mutual TLS (mTLS) with a distinct identity/certificate for each node.
- Pair nodes using a short-lived, single-use enrollment token exchanged out of
  band. The pairing operation verifies and pins the peer identity.
- Support certificate rotation and peer revocation.
- Apply explicit authorization scopes to remote operations.
- Rate-limit requests and validate all payloads.
- Keep metrics and internal management APIs separate from the public VPN listener.

The current TrustTunnel metrics endpoint itself has no authentication. It must
remain on loopback/private trusted networking and must not be exposed as the
cluster management API.

## Identity and audit

An administrator signs in to the local Manager UI. Manager-to-Manager requests
are authenticated with node identities, and carry a verifiable actor context
so the receiving node can record who initiated a remote action.

Every privileged operation must be audited on the owning node with:

- UTC timestamp;
- administrator identity;
- source node;
- target node;
- operation and affected resource;
- result and error details;
- configuration revision before and after the operation.

Node credentials must never be treated as administrator credentials.

## Monitoring and history

Each Manager can aggregate live snapshots from all configured peers.

- Every sample is tagged with NodeId.
- Dashboards support both cluster-wide totals and filtering by node.
- Offline peers are shown explicitly; old values must be marked stale, not
  displayed as current.
- The existing TrustTunnel counters are cumulative only for the lifetime of
  the endpoint process and reset after restart.
- Each Manager stores its own collected history initially. History replication
  is not required for the first multi-node release.
- Traffic rates are calculated from counter deltas, handling counter resets
  after endpoint restarts.

## Peer catalogue

The cluster catalogue must converge across the trusted nodes so that adding a
node from one Manager makes it visible in the other Managers. Membership changes
must be authenticated, versioned and auditable.

For the initial two-node implementation, a pairing workflow may establish
the bidirectional trust relationship and exchange the current node catalogue.
The model must not assume that there will only ever be two nodes.

## Initial implementation sequence

1. Introduce a stable NodeId and local-node settings.
2. Extract local endpoint operations behind application interfaces.
3. Add the internal node API and mTLS authentication.
4. Add peer pairing, listing, health and revocation.
5. Add a cluster view that aggregates monitoring snapshots by NodeId.
6. Route read-only remote queries to the owning peer.
7. Route privileged commands to the owning peer, with optimistic concurrency
   checks and audit.
8. Add history collection per node and traffic-rate calculation.

## Compatibility with the current dashboard

The first metrics dashboard remains the local-node view. It must be refactored
to use the same node abstraction that will later represent a remote peer.
Do not hard-code the assumption that the TrustTunnel endpoint is local to the
web UI into application/domain contracts.

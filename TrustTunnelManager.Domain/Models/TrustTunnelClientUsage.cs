namespace TrustTunnelManager.Domain.Models;

/// <summary>
/// Traffic counters and active-session information reported for one TrustTunnel client.
/// </summary>
public sealed record TrustTunnelClientUsage(
    string Username,
    string? IpAddress,
    long ActiveSessions,
    long InboundTrafficBytes,
    long OutboundTrafficBytes);

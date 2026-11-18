namespace TrustTunnelManager.Domain.Models;

/// <summary>
/// A point-in-time snapshot of the TrustTunnel endpoint metrics.
/// Traffic counters are cumulative since the endpoint process started.
/// </summary>
public sealed record TrustTunnelDashboardSnapshot(
    bool IsOnline,
    DateTimeOffset CollectedAtUtc,
    IReadOnlyDictionary<string, long> SessionsByProtocol,
    long InboundTrafficBytes,
    long OutboundTrafficBytes,
    long OutboundTcpSockets,
    long OutboundUdpSockets,
    IReadOnlyList<TrustTunnelClientUsage> Clients,
    bool PerClientMetricsEnabled,
    string? ErrorMessage = null,
    string? WarningMessage = null)
{
    public long ActiveSessions => SessionsByProtocol.Values.Sum();

    public static TrustTunnelDashboardSnapshot Offline(
        DateTimeOffset collectedAtUtc,
        string errorMessage) =>
        new(
            IsOnline: false,
            CollectedAtUtc: collectedAtUtc,
            SessionsByProtocol: new Dictionary<string, long>(),
            InboundTrafficBytes: 0,
            OutboundTrafficBytes: 0,
            OutboundTcpSockets: 0,
            OutboundUdpSockets: 0,
            Clients: [],
            PerClientMetricsEnabled: false,
            ErrorMessage: errorMessage);
}

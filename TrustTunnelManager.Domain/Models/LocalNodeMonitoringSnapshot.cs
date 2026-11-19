namespace TrustTunnelManager.Domain.Models;

/// <summary>
/// Read-only monitoring payload exposed to Home Assistant.
/// The watchdog state is serialized as a stable string rather than an enum ordinal.
/// </summary>
public sealed record LocalNodeMonitoringSnapshot(
    NodeInfo Node,
    ClientServiceSnapshot Service,
    LocalWatchdogMonitoringSnapshot Watchdog,
    DateTimeOffset CollectedAtUtc);

public sealed record LocalWatchdogMonitoringSnapshot(
    bool Enabled,
    string State,
    string InterfaceName,
    int ConsecutiveFailures,
    int FailureThreshold,
    DateTimeOffset? LastCheckAtUtc,
    bool? LastCheckSucceeded,
    string? LastCheckMessage,
    DateTimeOffset? LastRestartAtUtc,
    string? LastRestartMessage)
{
    public static LocalWatchdogMonitoringSnapshot From(WatchdogSnapshot snapshot) =>
        new(
            snapshot.Enabled,
            snapshot.State.ToString(),
            snapshot.InterfaceName,
            snapshot.ConsecutiveFailures,
            snapshot.FailureThreshold,
            snapshot.LastCheckAtUtc,
            snapshot.LastCheckSucceeded,
            snapshot.LastCheckMessage,
            snapshot.LastRestartAtUtc,
            snapshot.LastRestartMessage);
}

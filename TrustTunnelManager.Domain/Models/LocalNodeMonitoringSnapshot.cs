using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Domain.Models;

/// <summary>
/// Read-only monitoring payload exposed to Home Assistant.
/// </summary>
public sealed record LocalNodeMonitoringSnapshot(
    NodeInfo Node,
    ClientServiceSnapshot Service,
    WatchdogSnapshot? Watchdog,
    DateTimeOffset CollectedAtUtc);

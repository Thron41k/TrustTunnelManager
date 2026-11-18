using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Application.Abstractions;

/// <summary>
/// Reads monitoring information from a TrustTunnel endpoint.
/// </summary>
public interface ITrustTunnelMonitoringService
{
    Task<TrustTunnelDashboardSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);
}

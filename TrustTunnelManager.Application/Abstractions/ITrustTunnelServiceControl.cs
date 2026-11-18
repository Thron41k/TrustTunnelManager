using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Application.Abstractions;

public interface ITrustTunnelServiceControl
{
    Task<TrustTunnelServiceStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<TrustTunnelServiceResult> StartAsync(CancellationToken cancellationToken = default);

    Task<TrustTunnelServiceResult> StopAsync(CancellationToken cancellationToken = default);

    Task<TrustTunnelServiceResult> RestartAsync(CancellationToken cancellationToken = default);

    Task<TrustTunnelServiceResult> ReloadAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrustTunnelLogEntry>> GetRecentLogsAsync(
        int count = 200,
        CancellationToken cancellationToken = default);
}

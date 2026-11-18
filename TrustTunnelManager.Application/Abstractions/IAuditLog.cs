using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Application.Abstractions;

public interface IAuditLog
{
    Task WriteAsync(
        string actor,
        string action,
        string target,
        bool succeeded,
        string? details = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEvent>> GetRecentAsync(
        int count = 200,
        CancellationToken cancellationToken = default);
}

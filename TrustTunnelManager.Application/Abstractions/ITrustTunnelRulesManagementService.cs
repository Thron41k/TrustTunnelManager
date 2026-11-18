using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Application.Abstractions;

public interface ITrustTunnelRulesManagementService
{
    Task<IReadOnlyList<TrustTunnelAccessRule>> GetRulesAsync(
        CancellationToken cancellationToken = default);

    Task ReplaceRulesAsync(
        IReadOnlyList<TrustTunnelAccessRule> rules,
        CancellationToken cancellationToken = default);
}

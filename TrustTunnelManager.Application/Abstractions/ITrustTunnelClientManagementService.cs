using TrustTunnelManager.Application.Models;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Application.Abstractions;

public interface ITrustTunnelClientManagementService
{
    Task<IReadOnlyList<TrustTunnelClient>> GetClientsAsync(
        CancellationToken cancellationToken = default);

    Task<TrustTunnelClient> CreateClientAsync(
        CreateTrustTunnelClientCommand command,
        CancellationToken cancellationToken = default);

    Task<TrustTunnelClient> UpdateClientAsync(
        string username,
        UpdateTrustTunnelClientCommand command,
        CancellationToken cancellationToken = default);

    Task DeleteClientAsync(
        string username,
        CancellationToken cancellationToken = default);
}

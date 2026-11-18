using TrustTunnelManager.Application.Models;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Application.Abstractions;

public interface ITrustTunnelClientConfigurationExporter
{
    Task<ClientConfigurationExport> ExportAsync(
        string username,
        ClientConfigurationRequest request,
        CancellationToken cancellationToken = default);
}

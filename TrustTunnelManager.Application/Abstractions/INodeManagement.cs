using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Application.Abstractions;

public interface ILocalTrustTunnelClientManager
{
    Task<ClientServiceSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> ExecuteAsync(string action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NodeLogEntry>> GetLogsAsync(int count = 100, CancellationToken cancellationToken = default);
    Task<ClientConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> ApplyConfigurationAsync(
        string content,
        string expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface IPeerRegistry
{
    Task<IReadOnlyList<PeerRegistration>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PeerRegistration?> FindByNodeIdAsync(string nodeId, CancellationToken cancellationToken = default);
    Task<PeerRegistration?> FindByThumbprintAsync(string thumbprint, CancellationToken cancellationToken = default);
    Task AddOrUpdateAsync(NodeInfo node, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string nodeId, CancellationToken cancellationToken = default);
}

public interface IPairingInvitationStore
{
    Task<string> CreateAsync(CancellationToken cancellationToken = default);
    Task<bool> TryConsumeAsync(string token, CancellationToken cancellationToken = default);
}

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default);
}

public interface IPeerMeshService
{
    Task<IReadOnlyList<NodeSnapshot>> GetClusterSnapshotAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> ExecuteAsync(string nodeId, string action, string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NodeLogEntry>> GetLogsAsync(string nodeId, int count = 100, CancellationToken cancellationToken = default);
    Task<ClientConfigurationSnapshot> GetConfigurationAsync(string nodeId, CancellationToken cancellationToken = default);
    Task<OperationResult> ApplyConfigurationAsync(
        string nodeId, string content, string expectedRevision, string actor,
        CancellationToken cancellationToken = default);
    Task<string> CreateInvitationAsync(string actor, CancellationToken cancellationToken = default);
    Task<OperationResult> PairAsync(
        string baseUrl, string token, string expectedThumbprint, string actor,
        CancellationToken cancellationToken = default);
    Task<bool> RemovePeerAsync(string nodeId, string actor, CancellationToken cancellationToken = default);
}

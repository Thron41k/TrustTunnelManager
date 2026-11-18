namespace TrustTunnelManager.Domain.Models;

public sealed record NodeInfo(
    string NodeId,
    string Name,
    string BaseUrl,
    string CertificateThumbprint);

public sealed record PeerRegistration(NodeInfo Node, DateTimeOffset AddedAtUtc);

public sealed record ClientServiceSnapshot(
    bool IsSupported,
    bool IsInstalled,
    string ActiveState,
    string SubState,
    int? MainProcessId,
    long? MemoryBytes,
    long? CpuTimeNanoseconds,
    long? RxBytes,
    long? TxBytes,
    DateTimeOffset CollectedAtUtc,
    string? Error = null);

public sealed record NodeSnapshot(
    NodeInfo Node,
    bool IsLocal,
    bool IsOnline,
    bool IsStale,
    DateTimeOffset CollectedAtUtc,
    ClientServiceSnapshot? Service,
    string? Error = null);

public sealed record ClientConfigurationSnapshot(
    string Content,
    string Revision,
    DateTimeOffset LoadedAtUtc);

public sealed record NodeLogEntry(string Timestamp, string Message);

public sealed record OperationResult(bool Success, string Message);

public sealed record NodeJoinRequest(string InviteToken, NodeInfo Node);

public sealed record ConfigurationUpdate(string Content, string ExpectedRevision);

public sealed record AuditEntry(
    DateTimeOffset CreatedAtUtc,
    string Actor,
    string SourceNodeId,
    string TargetNodeId,
    string Action,
    bool Succeeded,
    string Details);

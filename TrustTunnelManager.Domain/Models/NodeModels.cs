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
    string? Error = null,
    DateTimeOffset? LastSeenAtUtc = null,
    WatchdogSnapshot? Watchdog = null);

public enum WatchdogState
{
    Disabled,
    Waiting,
    Healthy,
    Degraded,
    RestartCooldown,
    Restarting,
    Restarted,
    RestartFailed,
    Unsupported,
    ConfigurationError
}

public sealed record WatchdogSnapshot(
    bool Enabled,
    WatchdogState State,
    string InterfaceName,
    int ConsecutiveFailures,
    int FailureThreshold,
    DateTimeOffset? LastCheckAtUtc,
    bool? LastCheckSucceeded,
    string? LastCheckMessage,
    DateTimeOffset? LastRestartAtUtc,
    string? LastRestartMessage);

public sealed record NodeStatusResponse(
    ClientServiceSnapshot Service,
    WatchdogSnapshot? Watchdog);

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

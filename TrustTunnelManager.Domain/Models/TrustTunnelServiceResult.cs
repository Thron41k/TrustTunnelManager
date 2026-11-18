namespace TrustTunnelManager.Domain.Models;

public sealed record TrustTunnelServiceResult(bool Success, string Message);

public sealed record TrustTunnelLogEntry(string Timestamp, string Message);

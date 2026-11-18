namespace TrustTunnelManager.Domain.Models;

public sealed class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public string? Details { get; set; }
}

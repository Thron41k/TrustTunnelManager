namespace TrustTunnelManager.Domain.Models;

public sealed record ClientConfigurationExport(
    string Username,
    string Format,
    string Content,
    DateTimeOffset GeneratedAtUtc);

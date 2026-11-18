namespace TrustTunnelManager.Domain.Models;

public sealed record TrustTunnelAccessRule(
    int Order,
    string? Cidr,
    string? ClientRandomPrefix,
    string? ClientRandomAuthKey,
    string Action);

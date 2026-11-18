namespace TrustTunnelManager.Application.Models;

public sealed record ClientConfigurationRequest(
    string Format,
    IReadOnlyList<string> Addresses,
    string? DisplayName = null,
    string? CustomSni = null,
    string? ClientRandomPrefix = null,
    string? ClientRandomAuthKey = null,
    IReadOnlyList<string>? DnsUpstreams = null,
    bool SubscriptionOnly = false);

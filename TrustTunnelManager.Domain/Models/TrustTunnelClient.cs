namespace TrustTunnelManager.Domain.Models;

public sealed record TrustTunnelClient(
    string Username,
    uint? MaxHttp2Connections,
    uint? MaxHttp3Connections);

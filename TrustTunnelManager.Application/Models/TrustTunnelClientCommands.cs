namespace TrustTunnelManager.Application.Models;

public sealed record CreateTrustTunnelClientCommand(
    string Username,
    string Password,
    uint? MaxHttp2Connections,
    uint? MaxHttp3Connections);

public sealed record UpdateTrustTunnelClientCommand(
    string? NewPassword,
    uint? MaxHttp2Connections,
    uint? MaxHttp3Connections);

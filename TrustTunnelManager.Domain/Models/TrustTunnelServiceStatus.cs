namespace TrustTunnelManager.Domain.Models;

public sealed record TrustTunnelServiceStatus(
    bool IsSupported,
    bool IsInstalled,
    string ActiveState,
    string SubState,
    bool IsEnabled,
    int? MainProcessId,
    string? ErrorMessage = null)
{
    public bool IsActive =>
        string.Equals(ActiveState, "active", StringComparison.OrdinalIgnoreCase);
}

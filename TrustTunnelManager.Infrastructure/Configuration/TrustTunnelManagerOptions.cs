namespace TrustTunnelManager.Infrastructure.Configuration;

public sealed class TrustTunnelManagerOptions
{
    public const string SectionName = "TrustTunnel";

    public TrustTunnelPathsOptions Paths { get; set; } = new();

    public TrustTunnelServiceOptions Service { get; set; } = new();

    public ClientConfigurationOptions ClientConfiguration { get; set; } = new();
}

public sealed class TrustTunnelPathsOptions
{
    public string SettingsFile { get; set; } = "/opt/trusttunnel/vpn.toml";

    public string TlsHostsFile { get; set; } = "/opt/trusttunnel/hosts.toml";

    public string ExecutablePath { get; set; } = "/opt/trusttunnel/trusttunnel_endpoint";
}

public sealed class TrustTunnelServiceOptions
{
    public string UnitName { get; set; } = "trusttunnel.service";

    public bool UseSudo { get; set; } = true;

    public bool UseSudoForJournalctl { get; set; }

    public string SudoPath { get; set; } = "/usr/bin/sudo";

    public string SystemctlPath { get; set; } = "/usr/bin/systemctl";

    public string JournalctlPath { get; set; } = "/usr/bin/journalctl";

    public int CommandTimeoutSeconds { get; set; } = 15;
}

public sealed class ClientConfigurationOptions
{
    public string[] Addresses { get; set; } = [];

    public string? DisplayName { get; set; }

    public string[] DnsUpstreams { get; set; } = [];

    public int CommandTimeoutSeconds { get; set; } = 30;
}

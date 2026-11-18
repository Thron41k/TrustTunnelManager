using Microsoft.Extensions.Configuration;
using TrustTunnelManager.Infrastructure.Clients;
using TrustTunnelManager.Infrastructure.Mesh;
using Xunit;

namespace TrustTunnelManager.Tests;

public sealed class MeshSecurityTests
{
    [Fact]
    public async Task Pairing_invitation_is_single_use()
    {
        var store = new PairingInvitationStore();

        var token = await store.CreateAsync();

        Assert.True(await store.TryConsumeAsync(token));
        Assert.False(await store.TryConsumeAsync(token));
    }

    [Fact]
    public async Task Pairing_invitation_rejects_unknown_token()
    {
        var store = new PairingInvitationStore();

        Assert.False(await store.TryConsumeAsync("not-a-valid-invitation"));
    }

    [Fact]
    public void Peer_registry_accepts_https_origin_with_sha256_fingerprint()
    {
        var node = new TrustTunnelManager.Domain.Models.NodeInfo(
            Guid.NewGuid().ToString("D"),
            "node-b",
            "https://node-b.example.net:5080",
            new string('A', 64));

        PeerRegistry.ValidateNodeInfo(node);
    }

    [Fact]
    public void Peer_registry_rejects_non_https_address()
    {
        var node = new TrustTunnelManager.Domain.Models.NodeInfo(
            Guid.NewGuid().ToString("D"),
            "node-b",
            "http://node-b.example.net:5080",
            new string('A', 64));

        Assert.Throws<InvalidDataException>(() => PeerRegistry.ValidateNodeInfo(node));
    }

    [Fact]
    public void Peer_registry_rejects_malformed_fingerprint()
    {
        var node = new TrustTunnelManager.Domain.Models.NodeInfo(
            Guid.NewGuid().ToString("D"),
            "node-b",
            "https://node-b.example.net:5080",
            new string('Z', 64));

        Assert.Throws<InvalidDataException>(() => PeerRegistry.ValidateNodeInfo(node));
    }

    [Fact]
    public async Task Local_manager_rejects_arbitrary_commands()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TrustTunnelClient:ServiceUnit"] = "trusttunnel-client.service"
            })
            .Build();

        var manager = new SystemdTrustTunnelClientManager(configuration, new ProcessCommandRunner());

        var result = await manager.ExecuteAsync("sh -c 'id'");

        Assert.False(result.Success);
        Assert.Contains("start, stop и restart", result.Message);
    }

    [Fact]
    public async Task Process_runner_does_not_interpret_shell_metacharacters()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".marker");
        var payload = $"$(touch {marker})";

        var result = await new ProcessCommandRunner().RunAsync(
            "/usr/bin/printf",
            ["%s", payload],
            TimeSpan.FromSeconds(3));

        Assert.True(result.Success, result.StandardError);
        Assert.Equal(payload, result.StandardOutput);
        Assert.False(File.Exists(marker));
    }
}

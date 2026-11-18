using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Infrastructure.Mesh;

public sealed class PeerRequestAuthenticator(IPeerRegistry peers)
{
    public async Task<PeerRegistration?> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.IsHttps) return null;
        var cert = await context.Connection.GetClientCertificateAsync(ct);
        if (cert is null || DateTime.UtcNow < cert.NotBefore.ToUniversalTime() || DateTime.UtcNow > cert.NotAfter.ToUniversalTime()) return null;
        return await peers.FindByThumbprintAsync(LocalNodeIdentity.NormalizeThumbprint(
            cert.GetCertHashString(HashAlgorithmName.SHA256)), ct);
    }
}

public sealed class PeerMeshService : IPeerMeshService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LocalNodeIdentity _identity;
    private readonly IPeerRegistry _peers;
    private readonly IPairingInvitationStore _invitations;
    private readonly ILocalTrustTunnelClientManager _local;
    private readonly IAuditLog _audit;

    public PeerMeshService(LocalNodeIdentity identity, IPeerRegistry peers, IPairingInvitationStore invitations,
        ILocalTrustTunnelClientManager local, IAuditLog audit)
        => (_identity, _peers, _invitations, _local, _audit) = (identity, peers, invitations, local, audit);

    public async Task<IReadOnlyList<NodeSnapshot>> GetClusterSnapshotAsync(CancellationToken ct = default)
    {
        var local = await _local.GetStatusAsync(ct);
        var list = new List<NodeSnapshot> { new(_identity.Info, true, true, false, DateTimeOffset.UtcNow, local, local.Error) };
        var tasks = (await _peers.GetAllAsync(ct)).Select(async peer =>
        {
            try
            {
                var status = await SendAsync<ClientServiceSnapshot>(peer, HttpMethod.Get, "/internal/v1/status", null, ct);
                return new NodeSnapshot(peer.Node, false, true, false, DateTimeOffset.UtcNow, status);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return new NodeSnapshot(peer.Node, false, false, true, DateTimeOffset.UtcNow, null, "Недоступен: " + ex.Message);
            }
        });
        list.AddRange(await Task.WhenAll(tasks));
        return list;
    }

    public async Task<OperationResult> ExecuteAsync(string nodeId, string action, string actor, CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId)
        {
            var result = await _local.ExecuteAsync(action, ct);
            await AuditAsync(actor, nodeId, "service." + action, result, ct);
            return result;
        }
        var peer = await RequirePeerAsync(nodeId, ct);
        return await SendAsync<OperationResult>(peer, HttpMethod.Post, "/internal/v1/service/" + Uri.EscapeDataString(action), null, ct, actor);
    }

    public async Task<IReadOnlyList<NodeLogEntry>> GetLogsAsync(string nodeId, int count = 100, CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId) return await _local.GetLogsAsync(count, ct);
        return await SendAsync<List<NodeLogEntry>>(await RequirePeerAsync(nodeId, ct), HttpMethod.Get,
            "/internal/v1/logs?count=" + Math.Clamp(count, 1, 500), null, ct);
    }

    public async Task<ClientConfigurationSnapshot> GetConfigurationAsync(string nodeId, CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId) return await _local.GetConfigurationAsync(ct);
        return await SendAsync<ClientConfigurationSnapshot>(await RequirePeerAsync(nodeId, ct),
            HttpMethod.Get, "/internal/v1/configuration", null, ct);
    }

    public async Task<OperationResult> ApplyConfigurationAsync(string nodeId, string content, string expectedRevision,
        string actor, CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId)
        {
            var result = await _local.ApplyConfigurationAsync(content, expectedRevision, ct);
            await AuditAsync(actor, nodeId, "configuration.apply", result, ct);
            return result;
        }
        return await SendAsync<OperationResult>(await RequirePeerAsync(nodeId, ct), HttpMethod.Put,
            "/internal/v1/configuration", new ConfigurationUpdate(content, expectedRevision), ct, actor);
    }

    public Task<string> CreateInvitationAsync(CancellationToken ct = default) => _invitations.CreateAsync(ct);

    public async Task<OperationResult> PairAsync(string baseUrl, string token, string expectedThumbprint, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return new(false, "Для сопряжения нужен HTTPS URL без параметров.");
        var pin = LocalNodeIdentity.NormalizeThumbprint(expectedThumbprint);
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) return new(false, "Неверный SHA-256 fingerprint (нужно 64 hex-символа).");

        try
        {
            using var client = CreateClient(pin);
            var url = uri.ToString().TrimEnd('/') + "/internal/v1/pairing/accept";
            using var response = await client.PostAsJsonAsync(new Uri(url), new NodeJoinRequest(token, _identity.Info), JsonOptions, ct);
            if (!response.IsSuccessStatusCode) return new(false, $"Сопряжение отклонено: HTTP {(int)response.StatusCode}.");
            var remote = await response.Content.ReadFromJsonAsync<NodeInfo>(JsonOptions, ct);
            if (remote is null || remote.NodeId == _identity.Info.NodeId ||
                LocalNodeIdentity.NormalizeThumbprint(remote.CertificateThumbprint) != pin)
                return new(false, "Идентичность или fingerprint удалённого узла не совпадает.");
            await _peers.AddOrUpdateAsync(remote, ct);
            await AuditAsync("administrator", remote.NodeId, "peer.pair", new(true, "mTLS pairing completed"), ct);
            return new(true, $"Узел «{remote.Name}» добавлен.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new(false, "Ошибка сопряжения: " + ex.Message);
        }
    }

    public async Task<bool> RemovePeerAsync(string nodeId, CancellationToken ct = default)
    {
        var removed = await _peers.RemoveAsync(nodeId, ct);
        await AuditAsync("administrator", nodeId, "peer.revoke", new(removed, "Peer trust entry removed"), ct);
        return removed;
    }

    private async Task<PeerRegistration> RequirePeerAsync(string nodeId, CancellationToken ct) =>
        await _peers.FindByNodeIdAsync(nodeId, ct) ?? throw new InvalidOperationException("Узел не найден среди доверенных peers.");

    private async Task AuditAsync(string actor, string target, string action, OperationResult result, CancellationToken ct) =>
        await _audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, actor, _identity.Info.NodeId,
            target, action, result.Success, result.Message), ct);

    private async Task<T> SendAsync<T>(PeerRegistration peer, HttpMethod method, string path, object? body,
        CancellationToken ct, string? actor = null)
    {
        using var client = CreateClient(peer.Node.CertificateThumbprint);
        using var request = new HttpRequestMessage(method, new Uri(peer.Node.BaseUrl.TrimEnd('/') + path));
        request.Headers.Add("X-TTM-Source-Node", _identity.Info.NodeId);
        if (!string.IsNullOrWhiteSpace(actor)) request.Headers.Add("X-TTM-Actor", actor);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Peer returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Peer returned an empty response.");
    }

    private HttpClient CreateClient(string thumbprint)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(_identity.Certificate);
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null && DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() &&
            DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
            LocalNodeIdentity.NormalizeThumbprint(cert.GetCertHashString(HashAlgorithmName.SHA256)) ==
            LocalNodeIdentity.NormalizeThumbprint(thumbprint);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}

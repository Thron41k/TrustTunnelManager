using System.Collections.Concurrent;
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
        if (!context.Request.IsHttps)
            return null;

        var certificate = await context.Connection.GetClientCertificateAsync(ct);
        if (certificate is null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
            DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
            return null;

        var fingerprint = LocalNodeIdentity.NormalizeThumbprint(
            certificate.GetCertHashString(HashAlgorithmName.SHA256));
        return await peers.FindByThumbprintAsync(fingerprint, ct);
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
    private readonly ConcurrentDictionary<string, LastKnownPeerStatus> _lastKnown = new(StringComparer.OrdinalIgnoreCase);

    public PeerMeshService(
        LocalNodeIdentity identity,
        IPeerRegistry peers,
        IPairingInvitationStore invitations,
        ILocalTrustTunnelClientManager local,
        IAuditLog audit)
    {
        (_identity, _peers, _invitations, _local, _audit) =
            (identity, peers, invitations, local, audit);
    }

    public async Task<IReadOnlyList<NodeSnapshot>> GetClusterSnapshotAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var local = await _local.GetStatusAsync(ct);
        var snapshots = new List<NodeSnapshot>
        {
            new(_identity.Info, true, true, false, now, local, local.Error, now)
        };

        var peers = await _peers.GetAllAsync(ct);
        var tasks = peers.Select(peer => GetPeerSnapshotAsync(peer, ct));
        snapshots.AddRange(await Task.WhenAll(tasks));
        return snapshots;
    }

    private async Task<NodeSnapshot> GetPeerSnapshotAsync(PeerRegistration peer, CancellationToken ct)
    {
        try
        {
            var status = await SendAsync<ClientServiceSnapshot>(
                peer, HttpMethod.Get, "/internal/v1/status", null, ct);
            var lastSeen = DateTimeOffset.UtcNow;
            _lastKnown[peer.Node.NodeId] = new(status, lastSeen);
            return new(peer.Node, false, true, false, lastSeen, status, status.Error, lastSeen);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            _lastKnown.TryGetValue(peer.Node.NodeId, out var lastKnown);
            return new(
                peer.Node,
                false,
                false,
                true,
                DateTimeOffset.UtcNow,
                lastKnown?.Service,
                "Недоступен: " + exception.Message,
                lastKnown?.LastSeenAtUtc);
        }
    }

    public async Task<OperationResult> ExecuteAsync(
        string nodeId, string action, string actor, CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId)
        {
            var localResult = await _local.ExecuteAsync(action, ct);
            await AuditAsync(actor, nodeId, "service." + action, localResult, ct);
            return localResult;
        }

        var peer = await RequirePeerAsync(nodeId, ct);
        try
        {
            var result = await SendAsync<OperationResult>(
                peer, HttpMethod.Post, "/internal/v1/service/" + Uri.EscapeDataString(action), null, ct);
            await AuditAsync(actor, nodeId, "service." + action, result, ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPeerFailure(exception))
        {
            await AuditAsync(actor, nodeId, "service." + action,
                new(false, "Удалённый узел недоступен: " + exception.Message), ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<NodeLogEntry>> GetLogsAsync(
        string nodeId, int count = 100, CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId)
            return await _local.GetLogsAsync(count, ct);

        var peer = await RequirePeerAsync(nodeId, ct);
        return await SendAsync<List<NodeLogEntry>>(
            peer, HttpMethod.Get, "/internal/v1/logs?count=" + Math.Clamp(count, 1, 500), null, ct);
    }

    public async Task<ClientConfigurationSnapshot> GetConfigurationAsync(
        string nodeId, CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId)
            return await _local.GetConfigurationAsync(ct);

        var peer = await RequirePeerAsync(nodeId, ct);
        return await SendAsync<ClientConfigurationSnapshot>(
            peer, HttpMethod.Get, "/internal/v1/configuration", null, ct);
    }

    public async Task<OperationResult> ApplyConfigurationAsync(
        string nodeId, string content, string expectedRevision, string actor,
        CancellationToken ct = default)
    {
        if (nodeId == _identity.Info.NodeId)
        {
            var localResult = await _local.ApplyConfigurationAsync(content, expectedRevision, ct);
            await AuditAsync(actor, nodeId, "configuration.apply", localResult, ct);
            return localResult;
        }

        var peer = await RequirePeerAsync(nodeId, ct);
        try
        {
            var result = await SendAsync<OperationResult>(
                peer, HttpMethod.Put, "/internal/v1/configuration",
                new ConfigurationUpdate(content, expectedRevision), ct);
            await AuditAsync(actor, nodeId, "configuration.apply", result, ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPeerFailure(exception))
        {
            await AuditAsync(actor, nodeId, "configuration.apply",
                new(false, "Удалённый узел недоступен: " + exception.Message), ct);
            throw;
        }
    }

    public async Task<string> CreateInvitationAsync(string actor, CancellationToken ct = default)
    {
        var token = await _invitations.CreateAsync(ct);
        await AuditAsync(actor, _identity.Info.NodeId, "peer.invitation.create",
            new(true, "Создано одноразовое приглашение (срок действия 5 минут)."), ct);
        return token;
    }

    public async Task<OperationResult> PairAsync(
        string baseUrl, string token, string expectedThumbprint, string actor,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
        {
            return new(false, "Для сопряжения укажите HTTPS URL узла без пути и параметров.");
        }

        var pin = LocalNodeIdentity.NormalizeThumbprint(expectedThumbprint);
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit))
            return new(false, "Неверный SHA-256 fingerprint (требуется 64 hex-символа).");

        try
        {
            using var client = CreateClient(pin);
            var endpoint = new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/internal/v1/pairing/accept");
            using var response = await client.PostAsJsonAsync(
                endpoint, new NodeJoinRequest(token, _identity.Info), JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var failure = new OperationResult(false, $"Сопряжение отклонено: HTTP {(int)response.StatusCode}.");
                await AuditAsync(actor, uri.Authority, "peer.pair", failure, ct);
                return failure;
            }

            var remote = await response.Content.ReadFromJsonAsync<NodeInfo>(JsonOptions, ct);
            if (remote is null || remote.NodeId == _identity.Info.NodeId ||
                LocalNodeIdentity.NormalizeThumbprint(remote.CertificateThumbprint) != pin)
            {
                var failure = new OperationResult(false, "Идентификатор или fingerprint удалённого узла не совпадает.");
                await AuditAsync(actor, uri.Authority, "peer.pair", failure, ct);
                return failure;
            }

            await _peers.AddOrUpdateAsync(remote, ct);
            await AuditAsync(actor, remote.NodeId, "peer.pair",
                new(true, "Двустороннее mTLS-сопряжение завершено."), ct);
            return new(true, $"Узел «{remote.Name}» добавлен.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPeerFailure(exception) || exception is FormatException or InvalidDataException)
        {
            var failure = new OperationResult(false, "Ошибка сопряжения: " + exception.Message);
            await AuditAsync(actor, uri.Authority, "peer.pair", failure, ct);
            return failure;
        }
    }

    public async Task<bool> RemovePeerAsync(
        string nodeId, string actor, CancellationToken ct = default)
    {
        var removed = await _peers.RemoveAsync(nodeId, ct);
        if (removed)
            _lastKnown.TryRemove(nodeId, out _);

        await AuditAsync(actor, nodeId, "peer.revoke",
            new(removed, removed ? "Доверие к удалённому узлу отозвано." : "Узел не найден."), ct);
        return removed;
    }

    private async Task<PeerRegistration> RequirePeerAsync(string nodeId, CancellationToken ct) =>
        await _peers.FindByNodeIdAsync(nodeId, ct)
        ?? throw new InvalidOperationException("Узел не найден среди доверенных peers.");

    private Task AuditAsync(
        string actor, string target, string action, OperationResult result, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry(
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(actor) ? "administrator" : actor,
            _identity.Info.NodeId,
            target,
            action,
            result.Success,
            result.Message), ct);

    private async Task<T> SendAsync<T>(
        PeerRegistration peer, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var client = CreateClient(peer.Node.CertificateThumbprint);
        using var request = new HttpRequestMessage(
            method, new Uri(peer.Node.BaseUrl.TrimEnd('/') + path));

        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Peer «{peer.Node.Name}» вернул HTTP {(int)response.StatusCode}.");

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Peer вернул пустой ответ.");
    }

    private HttpClient CreateClient(string thumbprint)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(_identity.Certificate);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null &&
            DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
            DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
            string.Equals(
                LocalNodeIdentity.NormalizeThumbprint(certificate.GetCertHashString(HashAlgorithmName.SHA256)),
                LocalNodeIdentity.NormalizeThumbprint(thumbprint),
                StringComparison.Ordinal);

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static bool IsPeerFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException;

    private sealed record LastKnownPeerStatus(ClientServiceSnapshot Service, DateTimeOffset LastSeenAtUtc);
}

using System.Text.Json;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Infrastructure.Mesh;

public sealed class PeerRegistry : IPeerRegistry
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PeerRegistry(IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = configuration["Mesh:StateDirectory"] ?? "App_Data";
        if (!Path.IsPathRooted(directory))
            directory = Path.Combine(environment.ContentRootPath, directory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "peers.json");
    }

    public async Task<IReadOnlyList<PeerRegistration>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<PeerRegistration?> FindByNodeIdAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        var peers = await GetAllAsync(cancellationToken);
        return peers.FirstOrDefault(x => string.Equals(x.Node.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PeerRegistration?> FindByThumbprintAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        var normalized = LocalNodeIdentity.NormalizeThumbprint(thumbprint);
        var peers = await GetAllAsync(cancellationToken);
        return peers.FirstOrDefault(x => LocalNodeIdentity.NormalizeThumbprint(x.Node.CertificateThumbprint) == normalized);
    }

    public async Task AddOrUpdateAsync(NodeInfo node, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(node.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Peer BaseUrl must use HTTPS.");
        if (string.IsNullOrWhiteSpace(node.NodeId) || node.NodeId.Length > 64 ||
            string.IsNullOrWhiteSpace(node.CertificateThumbprint) ||
            LocalNodeIdentity.NormalizeThumbprint(node.CertificateThumbprint).Length != 64)
            throw new InvalidDataException("Peer identity or certificate fingerprint is invalid.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var peers = (await ReadAsync(cancellationToken)).ToList();
            peers.RemoveAll(x => x.Node.NodeId == node.NodeId ||
                LocalNodeIdentity.NormalizeThumbprint(x.Node.CertificateThumbprint) ==
                LocalNodeIdentity.NormalizeThumbprint(node.CertificateThumbprint));
            peers.Add(new PeerRegistration(node with
            {
                BaseUrl = uri.ToString().TrimEnd('/'),
                CertificateThumbprint = LocalNodeIdentity.NormalizeThumbprint(node.CertificateThumbprint)
            }, DateTimeOffset.UtcNow));
            await WriteAsync(peers, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var peers = (await ReadAsync(cancellationToken)).ToList();
            var removed = peers.RemoveAll(x => x.Node.NodeId == nodeId) > 0;
            if (removed) await WriteAsync(peers, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<PeerRegistration>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<PeerRegistration>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteAsync(List<PeerRegistration> peers, CancellationToken cancellationToken)
    {
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, peers, JsonOptions, cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class PairingInvitationStore : IPairingInvitationStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _tokens = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tokens[Hash(token)] = DateTimeOffset.UtcNow.Add(Lifetime);
        foreach (var expired in _tokens.Where(x => x.Value <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
            _tokens.TryRemove(expired, out _);
        return Task.FromResult(token);
    }

    public Task<bool> TryConsumeAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return Task.FromResult(false);
        var key = Hash(token);
        var valid = _tokens.TryRemove(key, out var expiry) && expiry > DateTimeOffset.UtcNow;
        return Task.FromResult(valid);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}

public sealed class FileAuditLog : IAuditLog
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAuditLog(IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = configuration["Mesh:StateDirectory"] ?? "App_Data";
        if (!Path.IsPathRooted(directory)) directory = Path.Combine(environment.ContentRootPath, directory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "audit.jsonl");
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(entry) + Environment.NewLine,
                cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
            return lines.TakeLast(Math.Clamp(count, 1, 1000))
                .Select(line => JsonSerializer.Deserialize<AuditEntry>(line))
                .Where(entry => entry is not null).Cast<AuditEntry>().Reverse().ToArray();
        }
        finally { _gate.Release(); }
    }
}

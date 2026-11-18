using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
        var directory = GetStateDirectory(configuration, environment);
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
        return peers.FirstOrDefault(peer =>
            string.Equals(peer.Node.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PeerRegistration?> FindByThumbprintAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        var normalized = LocalNodeIdentity.NormalizeThumbprint(thumbprint);
        var peers = await GetAllAsync(cancellationToken);
        return peers.FirstOrDefault(peer =>
            string.Equals(LocalNodeIdentity.NormalizeThumbprint(peer.Node.CertificateThumbprint),
                normalized, StringComparison.Ordinal));
    }

    public async Task AddOrUpdateAsync(NodeInfo node, CancellationToken cancellationToken = default)
    {
        ValidateNode(node);

        var normalizedNode = node with
        {
            Name = node.Name.Trim(),
            BaseUrl = new Uri(node.BaseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            CertificateThumbprint = LocalNodeIdentity.NormalizeThumbprint(node.CertificateThumbprint)
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var peers = (await ReadAsync(cancellationToken)).ToList();
            peers.RemoveAll(peer =>
                peer.Node.NodeId == normalizedNode.NodeId ||
                string.Equals(peer.Node.CertificateThumbprint, normalizedNode.CertificateThumbprint,
                    StringComparison.OrdinalIgnoreCase));

            peers.Add(new PeerRegistration(normalizedNode, DateTimeOffset.UtcNow));
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
            var removed = peers.RemoveAll(peer => peer.Node.NodeId == nodeId) > 0;
            if (removed)
                await WriteAsync(peers, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateNode(NodeInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!Guid.TryParse(node.NodeId, out _) ||
            string.IsNullOrWhiteSpace(node.Name) || node.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(node.BaseUrl) || node.BaseUrl.Length > 2048)
        {
            throw new InvalidDataException("Идентификатор, имя или адрес узла имеют неверный формат.");
        }

        if (!Uri.TryCreate(node.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
        {
            throw new InvalidDataException("Peer BaseUrl должен быть HTTPS URL узла без дополнительных компонентов.");
        }

        var thumbprint = LocalNodeIdentity.NormalizeThumbprint(node.CertificateThumbprint);
        if (thumbprint.Length != 64 || !thumbprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Fingerprint узла должен содержать 64 hex-символа SHA-256.");
    }

    private async Task<IReadOnlyList<PeerRegistration>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return [];

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<List<PeerRegistration>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteAsync(List<PeerRegistration> peers, CancellationToken cancellationToken)
    {
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                await JsonSerializer.SerializeAsync(stream, peers, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static string GetStateDirectory(IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = configuration["Mesh:StateDirectory"] ?? "App_Data";
        if (!Path.IsPathRooted(directory))
            directory = Path.Combine(environment.ContentRootPath, directory);

        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return directory;
    }
}

public sealed class PairingInvitationStore : IPairingInvitationStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new();

    public Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tokens[Hash(token)] = DateTimeOffset.UtcNow.Add(Lifetime);

        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _tokens.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            _tokens.TryRemove(expired, out _);

        return Task.FromResult(token);
    }

    public Task<bool> TryConsumeAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(false);

        var key = Hash(token);
        var valid = _tokens.TryRemove(key, out var expiry) && expiry > DateTimeOffset.UtcNow;
        return Task.FromResult(valid);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class FileAuditLog : IAuditLog
{
    private const long MaximumFileSizeBytes = 10 * 1024 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAuditLog(IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = configuration["Mesh:StateDirectory"] ?? "App_Data";
        if (!Path.IsPathRooted(directory))
            directory = Path.Combine(environment.ContentRootPath, directory);

        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _path = Path.Combine(directory, "audit.jsonl");
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumFileSizeBytes)
            {
                var previousPath = _path + ".1";
                if (File.Exists(previousPath))
                    File.Delete(previousPath);
                File.Move(_path, previousPath);
            }

            await using var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);

            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + Environment.NewLine);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(
        int count = 100, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = new List<AuditEntry>();
            foreach (var path in new[] { _path + ".1", _path })
            {
                if (!File.Exists(path))
                    continue;

                var lines = await File.ReadAllLinesAsync(path, cancellationToken);
                foreach (var line in lines)
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                        if (entry is not null)
                            entries.Add(entry);
                    }
                    catch (JsonException) { /* Ignore a partial/corrupt final line. */ }
                }
            }

            return entries
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .Take(Math.Clamp(count, 1, 1000))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }
}

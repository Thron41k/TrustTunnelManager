using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Infrastructure.Mesh;

public sealed class LocalNodeIdentity
{
    public NodeInfo Info { get; }
    public X509Certificate2 Certificate { get; }

    public LocalNodeIdentity(IConfiguration configuration, IHostEnvironment environment)
    {
        var certificatePath = configuration["Mesh:IdentityCertificatePath"];
        if (string.IsNullOrWhiteSpace(certificatePath))
            throw new InvalidOperationException("Укажите Mesh:IdentityCertificatePath — путь к PFX сертификату узла.");

        if (!Path.IsPathRooted(certificatePath))
            certificatePath = Path.Combine(environment.ContentRootPath, certificatePath);

        Certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath, configuration["Mesh:IdentityCertificatePassword"] ?? string.Empty,
            X509KeyStorageFlags.EphemeralKeySet);

        var now = DateTime.UtcNow;
        if (!Certificate.HasPrivateKey || now < Certificate.NotBefore.ToUniversalTime() ||
            now > Certificate.NotAfter.ToUniversalTime())
        {
            Certificate.Dispose();
            throw new InvalidOperationException("Сертификат mesh должен быть действующим и содержать закрытый ключ.");
        }

        var stateDirectory = configuration["Mesh:StateDirectory"] ?? "App_Data";
        if (!Path.IsPathRooted(stateDirectory))
            stateDirectory = Path.Combine(environment.ContentRootPath, stateDirectory);

        Directory.CreateDirectory(stateDirectory);
        SecureDirectory(stateDirectory);

        var identityPath = Path.Combine(stateDirectory, "node-identity.json");
        var nodeId = LoadOrCreateNodeId(identityPath);

        var name = configuration["Mesh:NodeName"];
        if (string.IsNullOrWhiteSpace(name))
            name = Environment.MachineName;
        name = name.Trim();
        if (name.Length > 128)
            throw new InvalidOperationException("Mesh:NodeName не должен превышать 128 символов.");

        var advertisedBaseUrl = configuration["Mesh:AdvertisedBaseUrl"];
        if (!Uri.TryCreate(advertisedBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "Mesh:AdvertisedBaseUrl должен быть HTTPS URL узла без пути, логина, query и fragment.");
        }

        Info = new NodeInfo(
            nodeId,
            name,
            uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            NormalizeThumbprint(Certificate.GetCertHashString(HashAlgorithmName.SHA256)));
    }

    public static string NormalizeThumbprint(string value) =>
        new((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) && character is not ':' and not '-')
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string LoadOrCreateNodeId(string identityPath)
    {
        if (File.Exists(identityPath))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(identityPath));
            var nodeId = existing.RootElement.GetProperty("nodeId").GetString();
            if (!Guid.TryParse(nodeId, out var id))
                throw new InvalidDataException("Сохранённый NodeId имеет неверный формат.");
            return id.ToString("D");
        }

        var newNodeId = Guid.NewGuid().ToString("D");
        var temporaryPath = identityPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, new { nodeId = newNodeId });
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, identityPath, overwrite: false);
            return newNodeId;
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static void SecureDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

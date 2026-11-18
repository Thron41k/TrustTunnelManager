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
            throw new InvalidOperationException("Set Mesh:IdentityCertificatePath to a PFX certificate path.");

        if (!Path.IsPathRooted(certificatePath))
            certificatePath = Path.Combine(environment.ContentRootPath, certificatePath);

        Certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath, configuration["Mesh:IdentityCertificatePassword"] ?? string.Empty,
            X509KeyStorageFlags.EphemeralKeySet);

        if (!Certificate.HasPrivateKey || DateTime.UtcNow < Certificate.NotBefore.ToUniversalTime()
            || DateTime.UtcNow > Certificate.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("The mesh identity certificate must be valid and contain a private key.");

        var stateDirectory = configuration["Mesh:StateDirectory"] ?? "App_Data";
        if (!Path.IsPathRooted(stateDirectory))
            stateDirectory = Path.Combine(environment.ContentRootPath, stateDirectory);
        Directory.CreateDirectory(stateDirectory);

        var identityPath = Path.Combine(stateDirectory, "node-identity.json");
        string nodeId;
        if (File.Exists(identityPath))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(identityPath));
            nodeId = existing.RootElement.GetProperty("nodeId").GetString() ?? string.Empty;
            if (!Guid.TryParse(nodeId, out _))
                throw new InvalidDataException("Persisted NodeId is invalid.");
        }
        else
        {
            nodeId = Guid.NewGuid().ToString("D");
            var temporary = identityPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { nodeId }));
            File.Move(temporary, identityPath, overwrite: true);
        }

        var name = configuration["Mesh:NodeName"];
        if (string.IsNullOrWhiteSpace(name)) name = Environment.MachineName;
        var baseUrl = configuration["Mesh:AdvertisedBaseUrl"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Mesh:AdvertisedBaseUrl must be an absolute HTTPS URL.");

        Info = new NodeInfo(nodeId, name.Trim(), uri.ToString().TrimEnd('/'),
            NormalizeThumbprint(Certificate.GetCertHashString(HashAlgorithmName.SHA256)));
    }

    public static string NormalizeThumbprint(string value) =>
        new(value.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-')
            .Select(char.ToUpperInvariant).ToArray());
}

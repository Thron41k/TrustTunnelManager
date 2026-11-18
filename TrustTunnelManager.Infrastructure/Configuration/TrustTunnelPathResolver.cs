using System.Text;
using Microsoft.Extensions.Options;
using Tomlyn;
using Tomlyn.Model;

namespace TrustTunnelManager.Infrastructure.Configuration;

public sealed class TrustTunnelPathResolver(IOptions<TrustTunnelManagerOptions> options)
{
    private readonly TrustTunnelManagerOptions _options = options.Value;

    public string SettingsFile => Path.GetFullPath(_options.Paths.SettingsFile);

    public string TlsHostsFile => Path.GetFullPath(_options.Paths.TlsHostsFile);

    public string ExecutablePath => Path.GetFullPath(_options.Paths.ExecutablePath);

    public async Task<string> ResolveCredentialsFileAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken);

        if (!settings.TryGetValue("credentials_file", out var configuredPath) ||
            configuredPath is not string path ||
            string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "В vpn.toml не задан credentials_file. Укажите путь к credentials.toml.");
        }

        return ResolveRelativePath(path);
    }

    public async Task<string> ResolveRulesFileAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken);

        if (!settings.TryGetValue("rules_file", out var configuredPath) ||
            configuredPath is not string path ||
            string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "Управление правилами недоступно: в vpn.toml не задан rules_file. " +
                "Сначала укажите путь к rules.toml.");
        }

        return ResolveRelativePath(path);
    }

    public async Task<bool> HasRulesFileConfiguredAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken);

        return settings.TryGetValue("rules_file", out var path) &&
               path is string stringPath &&
               !string.IsNullOrWhiteSpace(stringPath);
    }

    private async Task<TomlTable> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var path = SettingsFile;

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Не найден основной файл настроек TrustTunnel.", path);
        }

        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);

        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text)
                   ?? throw new InvalidDataException("Файл vpn.toml пуст.");
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Не удалось разобрать файл настроек TrustTunnel '{path}': {exception.Message}",
                exception);
        }
    }

    private string ResolveRelativePath(string path)
    {
        var normalized = Environment.ExpandEnvironmentVariables(path.Trim());

        return Path.GetFullPath(
            Path.IsPathRooted(normalized)
                ? normalized
                : Path.Combine(Path.GetDirectoryName(SettingsFile)!, normalized));
    }
}

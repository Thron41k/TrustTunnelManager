using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Application.Models;
using TrustTunnelManager.Domain.Models;
using TrustTunnelManager.Infrastructure.Configuration;
using TrustTunnelManager.Infrastructure.Processes;

namespace TrustTunnelManager.Infrastructure.Clients;

/// <summary>
/// Delegates client configuration generation to the official TrustTunnel endpoint
/// binary to keep exported TOML and deep links compatible with the installed version.
/// </summary>
public sealed partial class TrustTunnelClientConfigurationExporter(
    TrustTunnelPathResolver pathResolver,
    IOptions<TrustTunnelManagerOptions> options,
    ProcessCommandRunner runner)
    : ITrustTunnelClientConfigurationExporter
{
    private readonly TrustTunnelManagerOptions _options = options.Value;

    public async Task<ClientConfigurationExport> ExportAsync(
        string username,
        ClientConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!UsernamePattern().IsMatch(username))
        {
            throw new ArgumentException("Некорректное имя пользователя.", nameof(username));
        }

        var format = request.Format.Trim().ToLowerInvariant();

        if (format is not ("toml" or "deeplink"))
        {
            throw new ArgumentException("Формат должен быть toml или deeplink.", nameof(request));
        }

        if (request.SubscriptionOnly && format != "deeplink")
        {
            throw new ArgumentException("--subscription-only поддерживается только для deeplink.");
        }

        if (!string.IsNullOrWhiteSpace(request.ClientRandomPrefix) &&
            !string.IsNullOrWhiteSpace(request.ClientRandomAuthKey))
        {
            throw new ArgumentException("Нельзя одновременно задавать Client Random Prefix и Auth Key.");
        }

        var addresses = request.Addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .ToArray();

        if (addresses.Length == 0)
        {
            addresses = _options.ClientConfiguration.Addresses
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Select(address => address.Trim())
                .ToArray();
        }

        var dnsUpstreams = (request.DnsUpstreams ?? [])
            .Concat(_options.ClientConfiguration.DnsUpstreams)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var arguments = new List<string>
        {
            pathResolver.SettingsFile,
            pathResolver.TlsHostsFile,
            "--client_config",
            username,
            "--format",
            format
        };

        foreach (var address in addresses)
        {
            arguments.Add("--address");
            arguments.Add(address);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? _options.ClientConfiguration.DisplayName
            : request.DisplayName.Trim();

        AddOption(arguments, "--name", displayName);
        AddOption(arguments, "--custom-sni", request.CustomSni);
        AddOption(arguments, "--client-random-prefix", request.ClientRandomPrefix);
        AddOption(arguments, "--client-random-auth-key", request.ClientRandomAuthKey);

        foreach (var dns in dnsUpstreams)
        {
            arguments.Add("--dns-upstream");
            arguments.Add(dns);
        }

        if (request.SubscriptionOnly)
        {
            arguments.Add("--subscription-only");
        }

        var timeout = Math.Clamp(
            _options.ClientConfiguration.CommandTimeoutSeconds,
            5,
            120);

        var result = await runner.RunAsync(
            pathResolver.ExecutablePath,
            arguments,
            TimeSpan.FromSeconds(timeout),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? "Не удалось сформировать конфигурацию клиента."
                : result.StandardError.Trim();

            throw new InvalidOperationException(error);
        }

        var content = format == "deeplink"
            ? ExtractDeepLink(result.StandardOutput)
            : result.StandardOutput.Trim();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("TrustTunnel не вернул клиентскую конфигурацию.");
        }

        return new ClientConfigurationExport(
            username,
            format,
            content,
            DateTimeOffset.UtcNow);
    }

    private static void AddOption(List<string> arguments, string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add(option);
        arguments.Add(value.Trim());
    }

    private static string ExtractDeepLink(string output)
    {
        var link = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("tt://", StringComparison.OrdinalIgnoreCase));

        return link ?? throw new InvalidOperationException(
            "TrustTunnel не вернул ссылку tt:// в выводе команды.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.@+-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}

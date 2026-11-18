using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;
using TrustTunnelManager.Infrastructure.Configuration;
using TrustTunnelManager.Infrastructure.Processes;

namespace TrustTunnelManager.Infrastructure.Systemd;

/// <summary>
/// Controls only the configured TrustTunnel systemd unit. Commands are passed
/// with ArgumentList and never through a shell.
/// </summary>
public sealed partial class LinuxSystemdServiceControl(
    IOptions<TrustTunnelManagerOptions> options,
    ProcessCommandRunner runner)
    : ITrustTunnelServiceControl
{
    private readonly TrustTunnelServiceOptions _service = options.Value.Service;

    public async Task<TrustTunnelServiceStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new TrustTunnelServiceStatus(
                IsSupported: false,
                IsInstalled: false,
                ActiveState: "unsupported",
                SubState: string.Empty,
                IsEnabled: false,
                MainProcessId: null,
                ErrorMessage: "Управление systemd доступно только при запуске Manager на Linux.");
        }

        ValidateUnitName();

        var result = await RunSystemctlAsync(
            [
                "show",
                _service.UnitName,
                "--property=LoadState",
                "--property=ActiveState",
                "--property=SubState",
                "--property=UnitFileState",
                "--property=MainPID",
                "--no-pager"
            ],
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return new TrustTunnelServiceStatus(
                IsSupported: true,
                IsInstalled: false,
                ActiveState: "unknown",
                SubState: string.Empty,
                IsEnabled: false,
                MainProcessId: null,
                ErrorMessage: CleanError(result.StandardError, result.StandardOutput));
        }

        var properties = ParseProperties(result.StandardOutput);
        var loadState = GetProperty(properties, "LoadState");
        var activeState = GetProperty(properties, "ActiveState");
        var subState = GetProperty(properties, "SubState");
        var unitFileState = GetProperty(properties, "UnitFileState");
        var mainPidText = GetProperty(properties, "MainPID");

        return new TrustTunnelServiceStatus(
            IsSupported: true,
            IsInstalled: !string.Equals(loadState, "not-found", StringComparison.OrdinalIgnoreCase),
            ActiveState: string.IsNullOrWhiteSpace(activeState) ? "inactive" : activeState,
            SubState: subState,
            IsEnabled: unitFileState is "enabled" or "enabled-runtime",
            MainProcessId: int.TryParse(mainPidText, out var pid) && pid > 0 ? pid : null);
    }

    public Task<TrustTunnelServiceResult> StartAsync(CancellationToken cancellationToken = default) =>
        ExecuteServiceActionAsync("start", cancellationToken);

    public Task<TrustTunnelServiceResult> StopAsync(CancellationToken cancellationToken = default) =>
        ExecuteServiceActionAsync("stop", cancellationToken);

    public Task<TrustTunnelServiceResult> RestartAsync(CancellationToken cancellationToken = default) =>
        ExecuteServiceActionAsync("restart", cancellationToken);

    public async Task<TrustTunnelServiceResult> ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return UnsupportedResult();
        }

        ValidateUnitName();

        // TrustTunnel handles SIGHUP for TLS-host and subscription reloads.
        // The supplied service template does not define ExecReload, so use
        // systemctl kill against the main process instead of the reload verb.
        var result = await RunSystemctlAsync(
            ["kill", "--signal=HUP", "--kill-who=main", _service.UnitName],
            cancellationToken);

        return result.ExitCode == 0
            ? new TrustTunnelServiceResult(true, "SIGHUP отправлен основному процессу TrustTunnel.")
            : new TrustTunnelServiceResult(false, CleanError(result.StandardError, result.StandardOutput));
    }

    public async Task<IReadOnlyList<TrustTunnelLogEntry>> GetRecentLogsAsync(
        int count = 200,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Просмотр journald доступен только на Linux.");
        }

        ValidateUnitName();

        var safeCount = Math.Clamp(count, 1, 1000);
        var journalctlPath = GetExecutable(_service.JournalctlPath);
        var executable = journalctlPath;
        var arguments = new List<string>();

        if (_service.UseSudoForJournalctl)
        {
            executable = GetExecutable(_service.SudoPath);
            arguments.Add("-n");
            arguments.Add(journalctlPath);
        }

        arguments.Add("--unit");
        arguments.Add(_service.UnitName);
        arguments.Add("--lines");
        arguments.Add(safeCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--no-pager");
        arguments.Add("--output=short-iso-precise");

        var result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(GetTimeout()),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                CleanError(result.StandardError, result.StandardOutput));
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseLogLine)
            .ToArray();
    }

    private async Task<TrustTunnelServiceResult> ExecuteServiceActionAsync(
        string action,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return UnsupportedResult();
        }

        ValidateUnitName();

        var result = await RunSystemctlAsync(
            [action, _service.UnitName],
            cancellationToken);

        return result.ExitCode == 0
            ? new TrustTunnelServiceResult(true, $"systemctl {action} выполнен.")
            : new TrustTunnelServiceResult(false, CleanError(result.StandardError, result.StandardOutput));
    }

    private async Task<ProcessCommandResult> RunSystemctlAsync(
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken)
    {
        var systemctlPath = GetExecutable(_service.SystemctlPath);
        var executable = systemctlPath;
        var arguments = new List<string>();

        if (_service.UseSudo)
        {
            executable = GetExecutable(_service.SudoPath);
            arguments.Add("-n");
            arguments.Add(systemctlPath);
        }

        arguments.AddRange(commandArguments);

        return await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(GetTimeout()),
            cancellationToken);
    }

    private int GetTimeout() => Math.Clamp(_service.CommandTimeoutSeconds, 1, 60);

    private static string GetExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("Путь к системной команде должен быть абсолютным.");
        }

        return path;
    }

    private void ValidateUnitName()
    {
        if (!UnitNamePattern().IsMatch(_service.UnitName))
        {
            throw new InvalidOperationException(
                "Некорректное имя systemd unit. Разрешены только безопасные символы имени службы.");
        }
    }

    private static IReadOnlyDictionary<string, string> ParseProperties(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

    private static string GetProperty(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value : string.Empty;

    private static string CleanError(string standardError, string standardOutput)
    {
        var message = string.IsNullOrWhiteSpace(standardError)
            ? standardOutput
            : standardError;

        return string.IsNullOrWhiteSpace(message)
            ? "Команда systemd завершилась с ошибкой."
            : message.Trim();
    }

    private static TrustTunnelServiceResult UnsupportedResult() =>
        new(false, "Управление службой доступно только на Linux.");

    private static TrustTunnelLogEntry ParseLogLine(string line)
    {
        var separator = line.IndexOf(' ');

        return separator > 0
            ? new TrustTunnelLogEntry(line[..separator], line[(separator + 1)..])
            : new TrustTunnelLogEntry(string.Empty, line);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.@:-]+\.service$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitNamePattern();
}

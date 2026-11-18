using System.Globalization;
using System.Text.RegularExpressions;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Infrastructure.Clients;

/// <summary>
/// Monitors HTTPS connectivity through the configured VPN interface and
/// restarts only the configured TrustTunnel Client systemd unit.
/// </summary>
public sealed class TrustTunnelWatchdogService : BackgroundService, ITrustTunnelWatchdogStatus
{
    private static readonly Regex InterfaceNameRegex =
        new(@"^[A-Za-z0-9_.:-]{1,15}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILocalTrustTunnelClientManager _clientManager;
    private readonly ProcessCommandRunner _runner;
    private readonly ILogger<TrustTunnelWatchdogService> _logger;
    private readonly WatchdogOptions _options;
    private readonly bool _useSudo;
    private readonly string _sudoPath;
    private readonly object _stateLock = new();

    private int _consecutiveFailures;
    private DateTimeOffset? _lastRestartAtUtc;
    private string? _lastRestartMessage;
    private WatchdogSnapshot _snapshot;

    public TrustTunnelWatchdogService(
        IConfiguration configuration,
        ProcessCommandRunner runner,
        ILocalTrustTunnelClientManager clientManager,
        ILogger<TrustTunnelWatchdogService> logger)
    {
        _runner = runner;
        _clientManager = clientManager;
        _logger = logger;

        var enabled = configuration.GetValue("Watchdog:Enabled", true);
        var interfaceName = configuration["Watchdog:Interface"] ?? "tun0";
        if (enabled && !InterfaceNameRegex.IsMatch(interfaceName))
            throw new InvalidOperationException("Watchdog:Interface должен содержать корректное имя Linux-интерфейса.");

        var probeUrl = configuration["Watchdog:ProbeUrl"] ?? "https://www.cloudflare.com/cdn-cgi/trace";
        if (enabled && (!Uri.TryCreate(probeUrl, UriKind.Absolute, out var probeUri) ||
                        probeUri.Scheme != Uri.UriSchemeHttps ||
                        string.IsNullOrWhiteSpace(probeUri.Host) ||
                        !string.IsNullOrEmpty(probeUri.UserInfo) ||
                        !string.IsNullOrEmpty(probeUri.Fragment)))
        {
            throw new InvalidOperationException("Watchdog:ProbeUrl должен быть корректным HTTPS URL.");
        }

        var curlPath = configuration["Watchdog:CurlPath"] ?? "/usr/bin/curl";
        if (enabled && !Path.IsPathRooted(curlPath))
            throw new InvalidOperationException("Watchdog:CurlPath должен быть абсолютным путём.");

        _useSudo = configuration.GetValue("Watchdog:UseSudo", true);
        _sudoPath = configuration["Watchdog:SudoPath"] ?? "/usr/bin/sudo";
        if (enabled && _useSudo && !Path.IsPathRooted(_sudoPath))
            throw new InvalidOperationException("Watchdog:SudoPath должен быть абсолютным путём.");

        _options = new WatchdogOptions(
            enabled,
            interfaceName,
            probeUrl,
            curlPath,
            _sudoPath,
            _useSudo,
            ReadBounded(configuration, "Watchdog:InitialDelaySeconds", 120, 0, 3600),
            ReadBounded(configuration, "Watchdog:IntervalSeconds", 60, 10, 3600),
            ReadBounded(configuration, "Watchdog:FailureThreshold", 3, 1, 20),
            ReadBounded(configuration, "Watchdog:RestartCooldownSeconds", 300, 30, 86_400),
            ReadBounded(configuration, "Watchdog:ConnectTimeoutSeconds", 5, 1, 30),
            ReadBounded(configuration, "Watchdog:MaxTimeSeconds", 12, 2, 120));

        if (_options.MaxTimeSeconds <= _options.ConnectTimeoutSeconds)
            _options = _options with { MaxTimeSeconds = _options.ConnectTimeoutSeconds + 1 };

        _snapshot = new WatchdogSnapshot(
            enabled,
            enabled ? WatchdogState.Waiting : WatchdogState.Disabled,
            interfaceName,
            0,
            _options.FailureThreshold,
            null,
            null,
            enabled ? "Ожидание первой проверки." : "Watchdog отключён настройкой.",
            null,
            null);
    }

    public WatchdogSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("TrustTunnel VPN watchdog is disabled by configuration.");
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            Publish(WatchdogState.Unsupported, null, null, "Watchdog поддерживается только в Linux.");
            _logger.LogWarning("TrustTunnel VPN watchdog is supported only on Linux.");
            return;
        }

        _logger.LogInformation(
            "TrustTunnel VPN watchdog started. Interface={Interface}, Interval={IntervalSeconds}s, FailureThreshold={FailureThreshold}, RestartCooldown={RestartCooldownSeconds}s.",
            _options.InterfaceName,
            _options.IntervalSeconds,
            _options.FailureThreshold,
            _options.RestartCooldownSeconds);

        try
        {
            if (_options.InitialDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(_options.InitialDelaySeconds), stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));
            await CheckSafelyAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("TrustTunnel VPN watchdog is stopping.");
        }
    }

    private async Task CheckSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var checkedAt = DateTimeOffset.UtcNow;
            _logger.LogError(exception, "Unexpected error in TrustTunnel VPN watchdog.");
            Publish(WatchdogState.Degraded, checkedAt, false,
                "Внутренняя ошибка watchdog: " + Truncate(exception.Message));
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var result = await ProbeAsync(cancellationToken);
        var checkedAt = DateTimeOffset.UtcNow;

        if (result.Success)
        {
            var previousState = GetSnapshot().State;
            _consecutiveFailures = 0;
            Publish(WatchdogState.Healthy, checkedAt, true,
                $"HTTPS через интерфейс {_options.InterfaceName} доступен.");

            if (previousState != WatchdogState.Healthy)
                _logger.LogInformation("VPN health check succeeded through {Interface}.", _options.InterfaceName);

            return;
        }

        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, int.MaxValue);
        var failureMessage = FirstNonEmpty(
            result.StandardError,
            result.StandardOutput,
            $"curl завершился с кодом {result.ExitCode}.");
        failureMessage = Truncate(failureMessage);

        _logger.LogWarning(
            "VPN health check failed ({Failures}/{Threshold}) through {Interface}: {Failure}",
            _consecutiveFailures,
            _options.FailureThreshold,
            _options.InterfaceName,
            failureMessage);

        if (_consecutiveFailures < _options.FailureThreshold)
        {
            Publish(WatchdogState.Degraded, checkedAt, false, failureMessage);
            return;
        }

        if (_lastRestartAtUtc is { } lastRestartAt)
        {
            var nextAllowedRestart = lastRestartAt.AddSeconds(_options.RestartCooldownSeconds);
            if (checkedAt < nextAllowedRestart)
            {
                var remainingSeconds = Math.Max(1, (int)Math.Ceiling((nextAllowedRestart - checkedAt).TotalSeconds));
                Publish(WatchdogState.RestartCooldown, checkedAt, false,
                    $"VPN недоступен. Повторный перезапуск будет разрешён через {remainingSeconds} сек. Последняя ошибка: {failureMessage}");
                return;
            }
        }

        // Set the cooldown before invoking systemctl, matching the old watchdog's
        // behavior and preventing rapid restart loops when systemctl itself fails.
        _lastRestartAtUtc = checkedAt;
        _lastRestartMessage = "Перезапуск службы выполняется.";
        Publish(WatchdogState.Restarting, checkedAt, false, failureMessage);

        OperationResult restartResult;
        try
        {
            restartResult = await _clientManager.ExecuteAsync("restart", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            restartResult = new OperationResult(false, exception.Message);
        }

        _lastRestartMessage = restartResult.Message;
        if (restartResult.Success)
        {
            Publish(WatchdogState.Restarted, checkedAt, false,
                $"VPN недоступен после {_consecutiveFailures} проверок. Команда перезапуска клиента выполнена.");
            _logger.LogWarning("TrustTunnel Client service restart was requested by the VPN watchdog.");
        }
        else
        {
            Publish(WatchdogState.RestartFailed, checkedAt, false,
                $"Не удалось перезапустить TrustTunnel Client: {Truncate(restartResult.Message)}");
            _logger.LogError("VPN watchdog could not restart TrustTunnel Client: {Message}", restartResult.Message);
        }
    }

    private async Task<CommandResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "--interface", _options.InterfaceName,
            "--connect-timeout", _options.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--max-time", _options.MaxTimeSeconds.ToString(CultureInfo.InvariantCulture),
            "--silent",
            "--show-error",
            "--fail",
            "--output", "/dev/null",
            "--url", _options.ProbeUrl
        };

        var executable = _options.UseSudo ? _options.SudoPath : _options.CurlPath;
        var commandArguments = _options.UseSudo
            ? new[] { "--", _options.CurlPath }.Concat(arguments)
            : arguments;

        return await _runner.RunAsync(
            executable,
            commandArguments,
            TimeSpan.FromSeconds(_options.MaxTimeSeconds + 5),
            cancellationToken);
    }

    private void Publish(
        WatchdogState state,
        DateTimeOffset? checkedAt,
        bool? checkSucceeded,
        string? message)
    {
        lock (_stateLock)
        {
            Volatile.Write(ref _snapshot, new WatchdogSnapshot(
                _options.Enabled,
                state,
                _options.InterfaceName,
                _consecutiveFailures,
                _options.FailureThreshold,
                checkedAt,
                checkSucceeded,
                message,
                _lastRestartAtUtc,
                _lastRestartMessage));
        }
    }

    private static int ReadBounded(
        IConfiguration configuration, string key, int defaultValue, int minimum, int maximum) =>
        Math.Clamp(configuration.GetValue(key, defaultValue), minimum, maximum);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Неизвестная ошибка.";

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400];

    private sealed record WatchdogOptions(
        bool Enabled,
        string InterfaceName,
        string ProbeUrl,
        string CurlPath,
        string SudoPath,
        bool UseSudo,
        int InitialDelaySeconds,
        int IntervalSeconds,
        int FailureThreshold,
        int RestartCooldownSeconds,
        int ConnectTimeoutSeconds,
        int MaxTimeSeconds);
}

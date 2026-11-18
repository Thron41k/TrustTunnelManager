using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Infrastructure.Clients;

public sealed partial class SystemdTrustTunnelClientManager : ILocalTrustTunnelClientManager
{
    private const int MaximumConfigurationLength = 1_000_000;
    private readonly IConfiguration _configuration;
    private readonly ProcessCommandRunner _runner;
    private readonly SemaphoreSlim _configGate = new(1, 1);

    public SystemdTrustTunnelClientManager(IConfiguration configuration, ProcessCommandRunner runner)
        => (_configuration, _runner) = (configuration, runner);

    public async Task<ClientServiceSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new(false, false, "unsupported", "unsupported", null, null, null, null, null,
                DateTimeOffset.UtcNow, "Управление systemd доступно только в Linux.");
        }

        var result = await RunSystemctlAsync(
            ["show", ServiceUnit(), "--no-pager",
             "--property=LoadState,ActiveState,SubState,MainPID,MemoryCurrent,CPUUsageNSec"], ct);

        if (!result.Success)
        {
            return new(true, false, "unknown", "unknown", null, null, null, null, null,
                DateTimeOffset.UtcNow, FirstNonEmpty(result.StandardError, result.StandardOutput));
        }

        var values = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        long? Number(string key) =>
            values.TryGetValue(key, out var value) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        var (rxBytes, txBytes) = ReadNetworkCounters(_configuration["TrustTunnelClient:NetworkInterface"]);
        return new(
            true,
            !string.Equals(values.GetValueOrDefault("LoadState"), "not-found", StringComparison.OrdinalIgnoreCase),
            values.GetValueOrDefault("ActiveState", "unknown"),
            values.GetValueOrDefault("SubState", "unknown"),
            Number("MainPID") is { } pid && pid is >= 0 and <= int.MaxValue ? (int)pid : null,
            Number("MemoryCurrent"),
            Number("CPUUsageNSec"),
            rxBytes,
            txBytes,
            DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> ExecuteAsync(string action, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return new(false, "systemd доступен только в Linux.");

        if (action is not ("start" or "stop" or "restart"))
            return new(false, "Разрешены только операции start, stop и restart.");

        var result = await RunSystemctlAsync([action, ServiceUnit()], ct);
        return result.Success
            ? new(true, $"Команда {action} выполнена.")
            : new(false, FirstNonEmpty(result.StandardError, result.StandardOutput, "systemctl завершился с ошибкой."));
    }

    public async Task<IReadOnlyList<NodeLogEntry>> GetLogsAsync(int count = 100, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return [];

        var safeCount = Math.Clamp(count, 1, 500);
        var result = await RunJournalctlAsync(
            ["-u", ServiceUnit(), "-n", safeCount.ToString(CultureInfo.InvariantCulture),
             "--no-pager", "-o", "short-iso-precise"], ct);

        if (!result.Success)
            return [new NodeLogEntry(DateTimeOffset.UtcNow.ToString("O"), FirstNonEmpty(result.StandardError, result.StandardOutput))];

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseJournalLine)
            .ToArray();
    }

    public async Task<ClientConfigurationSnapshot> GetConfigurationAsync(CancellationToken ct = default)
    {
        var path = ConfigPath();
        if (File.Exists(path) && new FileInfo(path).Length > MaximumConfigurationLength)
            throw new InvalidOperationException("Файл конфигурации превышает допустимый размер 1 МБ.");

        var content = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;
        return new(content, Revision(content), DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> ApplyConfigurationAsync(
        string content, string expectedRevision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaximumConfigurationLength)
            return new(false, "Конфигурация пуста или превышает 1 МБ.");

        if (string.IsNullOrEmpty(expectedRevision) || expectedRevision.Length != 64 || !expectedRevision.All(Uri.IsHexDigit))
            return new(false, "Некорректная revision конфигурации. Перечитайте файл перед сохранением.");

        await _configGate.WaitAsync(ct);
        string? temporaryPath = null;
        string? backupPath = null;
        var installed = false;
        var originalExists = false;
        UnixFileMode? originalMode = null;
        var path = ConfigPath();

        try
        {
            var current = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;
            var expectedBytes = Convert.FromHexString(expectedRevision);
            var actualBytes = Convert.FromHexString(Revision(current));

            if (!CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
                return new(false, "Конфигурация изменилась на узле. Перечитайте файл и повторите сохранение.");

            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return new(false, "Запись в символическую ссылку конфигурации запрещена.");

            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Не удалось определить каталог конфигурации.");
            Directory.CreateDirectory(directory);

            originalExists = File.Exists(path);
            if (originalExists && OperatingSystem.IsLinux())
                originalMode = File.GetUnixFileMode(path);

            if (originalExists)
            {
                backupPath = $"{path}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak";
                File.Copy(path, backupPath, overwrite: false);
                if (originalMode.HasValue && OperatingSystem.IsLinux())
                    File.SetUnixFileMode(backupPath, originalMode.Value);
            }

            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temporaryPath, originalMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));

                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
            installed = true;

            // Do not cancel the restart/rollback sequence after the new file has been installed.
            var restart = await ExecuteAsync("restart", CancellationToken.None);
            if (!restart.Success)
            {
                await RestorePreviousConfigurationAsync(path, backupPath, originalExists, originalMode);
                installed = false;
                await ExecuteAsync("restart", CancellationToken.None);
                return new(false, $"Перезапуск не выполнен. Предыдущая конфигурация восстановлена. {restart.Message}");
            }

            PruneBackups(path, keep: 10);
            return new(true, backupPath is null
                ? "Конфигурация сохранена, служба перезапущена."
                : $"Конфигурация сохранена, служба перезапущена. Резервная копия: {Path.GetFileName(backupPath)}");
        }
        catch (Exception exception)
        {
            var message = exception.Message;
            if (installed)
            {
                try
                {
                    await RestorePreviousConfigurationAsync(path, backupPath, originalExists, originalMode);
                    await ExecuteAsync("restart", CancellationToken.None);
                    message += " Предыдущая конфигурация восстановлена.";
                }
                catch (Exception rollbackException)
                {
                    message += $" Ошибка отката: {rollbackException.Message}";
                }
            }

            return new(false, $"Не удалось применить конфигурацию: {message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { /* Cleanup must not mask the operation result. */ }
            }

            _configGate.Release();
        }
    }

    private Task RestorePreviousConfigurationAsync(
        string path, string? backupPath, bool originalExists, UnixFileMode? originalMode)
    {
        if (originalExists && backupPath is not null)
        {
            File.Copy(backupPath, path, overwrite: true);
            if (originalMode.HasValue && OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, originalMode.Value);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private async Task<CommandResult> RunSystemctlAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var binary = _configuration["TrustTunnelClient:SystemctlPath"] ?? "/usr/bin/systemctl";
        var useSudo = _configuration.GetValue("TrustTunnelClient:UseSudo", true);
        var executable = useSudo ? _configuration["TrustTunnelClient:SudoPath"] ?? "/usr/bin/sudo" : binary;
        var commandArguments = useSudo ? new[] { "--", binary }.Concat(arguments) : arguments;

        return await _runner.RunAsync(executable, commandArguments,
            CommandTimeout(), ct);
    }

    private async Task<CommandResult> RunJournalctlAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var binary = _configuration["TrustTunnelClient:JournalctlPath"] ?? "/usr/bin/journalctl";
        var useSudo = _configuration.GetValue("TrustTunnelClient:UseSudoForJournalctl", true);
        var executable = useSudo ? _configuration["TrustTunnelClient:SudoPath"] ?? "/usr/bin/sudo" : binary;
        var commandArguments = useSudo ? new[] { "--", binary }.Concat(arguments) : arguments;

        return await _runner.RunAsync(executable, commandArguments,
            CommandTimeout(), ct);
    }

    private TimeSpan CommandTimeout() =>
        TimeSpan.FromSeconds(Math.Clamp(
            _configuration.GetValue("TrustTunnelClient:CommandTimeoutSeconds", 15), 3, 120));

    private string ServiceUnit()
    {
        var unit = _configuration["TrustTunnelClient:ServiceUnit"] ?? "trusttunnel-client.service";
        if (!UnitRegex().IsMatch(unit))
            throw new InvalidOperationException("Некорректное имя systemd unit в TrustTunnelClient:ServiceUnit.");
        return unit;
    }

    private string ConfigPath() =>
        Path.GetFullPath(_configuration["TrustTunnelClient:ConfigurationFile"] ?? "/etc/trusttunnel/client.toml");

    private static string Revision(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static (long? Rx, long? Tx) ReadNetworkCounters(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName) ||
            !Regex.IsMatch(interfaceName, "^[A-Za-z0-9_.:-]{1,15}$", RegexOptions.CultureInvariant))
            return (null, null);

        try
        {
            var statisticsPath = Path.Combine("/sys/class/net", interfaceName, "statistics");
            return (
                ReadCounter(Path.Combine(statisticsPath, "rx_bytes")),
                ReadCounter(Path.Combine(statisticsPath, "tx_bytes")));
        }
        catch (IOException) { return (null, null); }
        catch (UnauthorizedAccessException) { return (null, null); }
    }

    private static long? ReadCounter(string path) =>
        long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static NodeLogEntry ParseJournalLine(string line)
    {
        var separator = line.IndexOf(' ');
        return separator > 0
            ? new NodeLogEntry(line[..separator], line[(separator + 1)..])
            : new NodeLogEntry(string.Empty, line);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Неизвестная ошибка.";

    private static void PruneBackups(string path, int keep)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null) return;

        var prefix = Path.GetFileName(path) + ".";
        var backups = new DirectoryInfo(directory).EnumerateFiles(prefix + "*.bak")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(keep);

        foreach (var backup in backups)
        {
            try { backup.Delete(); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.@:-]{1,128}\.service$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitRegex();
}

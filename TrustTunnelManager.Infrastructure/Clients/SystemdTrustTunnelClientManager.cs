using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Infrastructure.Clients;

public sealed partial class SystemdTrustTunnelClientManager : ILocalTrustTunnelClientManager
{
    private readonly IConfiguration _configuration;
    private readonly ProcessCommandRunner _runner;
    private readonly SemaphoreSlim _configGate = new(1, 1);

    public SystemdTrustTunnelClientManager(IConfiguration configuration, ProcessCommandRunner runner)
    {
        _configuration = configuration;
        _runner = runner;
    }

    public async Task<ClientServiceSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return new(false, false, "unsupported", "unsupported", null, null, null, null, null, DateTimeOffset.UtcNow, "Linux only.");
        var result = await RunSystemctlAsync(["show", ServiceUnit(), "--no-pager",
            "--property=LoadState,ActiveState,SubState,MainPID,MemoryCurrent,CPUUsageNSec"], ct);
        if (!result.Success)
            return new(true, false, "unknown", "unknown", null, null, null, null, null, DateTimeOffset.UtcNow, result.StandardError);

        var values = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
        long? Number(string key) => values.TryGetValue(key, out var value) && long.TryParse(value, out var n) ? n : null;
        var (rx, tx) = ReadNetworkCounters(_configuration["TrustTunnelClient:NetworkInterface"]);
        return new(true, values.GetValueOrDefault("LoadState") != "not-found",
            values.GetValueOrDefault("ActiveState", "unknown"), values.GetValueOrDefault("SubState", "unknown"),
            (int?)Number("MainPID"), Number("MemoryCurrent"), Number("CPUUsageNSec"), rx, tx, DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> ExecuteAsync(string action, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) return new(false, "systemd доступен только в Linux.");
        if (action is not ("start" or "stop" or "restart")) return new(false, "Разрешены start, stop и restart.");
        var result = await RunSystemctlAsync([action, ServiceUnit()], ct);
        return result.Success ? new(true, $"Команда {action} выполнена.")
            : new(false, string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
    }

    public async Task<IReadOnlyList<NodeLogEntry>> GetLogsAsync(int count = 100, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) return [];
        var args = new[] { "-u", ServiceUnit(), "-n", Math.Clamp(count, 1, 500).ToString(), "--no-pager", "-o", "short-iso-precise" };
        var result = await RunJournalctlAsync(args, ct);
        if (!result.Success) return [new NodeLogEntry(DateTimeOffset.UtcNow.ToString("O"), result.StandardError)];
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new NodeLogEntry(line.Length > 30 ? line[..30] : "", line.Length > 31 ? line[31..] : line)).ToArray();
    }

    public async Task<ClientConfigurationSnapshot> GetConfigurationAsync(CancellationToken ct = default)
    {
        var path = ConfigPath();
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;
        return new(text, Revision(text), DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> ApplyConfigurationAsync(string content, string expectedRevision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1_000_000) return new(false, "Конфигурация пуста или больше 1 МБ.");
        await _configGate.WaitAsync(ct);
        try
        {
            var path = ConfigPath();
            var current = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Revision(current)), Convert.FromHexString(expectedRevision)))
                return new(false, "Конфигурация изменилась на узле. Перечитайте файл и повторите.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var exists = File.Exists(path);
            var backup = path + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
            UnixFileMode? mode = exists && OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : null;
            if (exists) File.Copy(path, backup);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
            if (mode.HasValue && OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, mode.Value);
            File.Move(temp, path, true);
            var restart = await ExecuteAsync("restart", CancellationToken.None);
            if (restart.Success) return restart;
            if (exists) File.Copy(backup, path, true); else if (File.Exists(path)) File.Delete(path);
            await ExecuteAsync("restart", CancellationToken.None);
            return new(false, $"Restart failed; previous configuration restored. {restart.Message}");
        }
        catch (Exception ex) { return new(false, ex.Message); }
        finally { _configGate.Release(); }
    }

    private async Task<CommandResult> RunSystemctlAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var sudo = _configuration.GetValue("TrustTunnelClient:UseSudo", true);
        var binary = _configuration["TrustTunnelClient:SystemctlPath"] ?? "/usr/bin/systemctl";
        var executable = sudo ? _configuration["TrustTunnelClient:SudoPath"] ?? "/usr/bin/sudo" : binary;
        var commandArgs = sudo ? new[] { "--", binary }.Concat(args) : args;
        return await _runner.RunAsync(executable, commandArgs,
            TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue("TrustTunnelClient:CommandTimeoutSeconds", 15), 3, 120)), ct);
    }

    private async Task<CommandResult> RunJournalctlAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var sudo = _configuration.GetValue("TrustTunnelClient:UseSudoForJournalctl", true);
        var binary = _configuration["TrustTunnelClient:JournalctlPath"] ?? "/usr/bin/journalctl";
        var executable = sudo ? _configuration["TrustTunnelClient:SudoPath"] ?? "/usr/bin/sudo" : binary;
        var commandArgs = sudo ? new[] { "--", binary }.Concat(args) : args;
        return await _runner.RunAsync(executable, commandArgs,
            TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue("TrustTunnelClient:CommandTimeoutSeconds", 15), 3, 120)), ct);
    }

    private string ServiceUnit()
    {
        var unit = _configuration["TrustTunnelClient:ServiceUnit"] ?? "trusttunnel-client.service";
        if (!UnitRegex().IsMatch(unit)) throw new InvalidOperationException("Invalid systemd service unit.");
        return unit;
    }
    private string ConfigPath() => Path.GetFullPath(_configuration["TrustTunnelClient:ConfigurationFile"] ?? "/etc/trusttunnel/client.toml");
    private static string Revision(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    private static (long?, long?) ReadNetworkCounters(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, "^[A-Za-z0-9_.:-]{1,15}$")) return (null, null);
        try
        {
            var root = Path.Combine("/sys/class/net", name, "statistics");
            return (ReadCounter(Path.Combine(root, "rx_bytes")), ReadCounter(Path.Combine(root, "tx_bytes")));
        }
        catch { return (null, null); }
    }
    private static long? ReadCounter(string path) => long.TryParse(File.ReadAllText(path).Trim(), out var n) ? n : null;

    [GeneratedRegex("^[A-Za-z0-9_.@:-]{1,128}\.service$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitRegex();
}

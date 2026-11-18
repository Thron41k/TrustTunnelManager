using System.Diagnostics;

namespace TrustTunnelManager.Infrastructure.Clients;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed class ProcessCommandRunner
{
    public async Task<CommandResult> RunAsync(
        string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return new(-1, string.Empty, "Failed to start process.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new(-1, string.Empty, "Command timed out.");
        }
        catch (Exception exception) { return new(-1, string.Empty, exception.Message); }
    }
}

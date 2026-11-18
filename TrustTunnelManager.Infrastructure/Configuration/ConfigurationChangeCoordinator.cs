using System.Text;
using Microsoft.Extensions.Logging;
using TrustTunnelManager.Application.Abstractions;

namespace TrustTunnelManager.Infrastructure.Configuration;

/// <summary>
/// Serializes configuration changes, preserves the original file and rolls back
/// when TrustTunnel cannot be restarted with the new configuration.
/// </summary>
public sealed class ConfigurationChangeCoordinator(
    ITrustTunnelServiceControl serviceControl,
    ILogger<ConfigurationChangeCoordinator> logger)
{
    private static readonly SemaphoreSlim MutationLock = new(1, 1);

    public async Task ApplyAsync(
        string path,
        Func<string, string> transform,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(transform);

        await MutationLock.WaitAsync(cancellationToken);

        try
        {
            var fullPath = Path.GetFullPath(path);
            var existed = File.Exists(fullPath);
            var original = existed
                ? await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken)
                : string.Empty;

            // Transformation and validation happen before any write.
            var updated = transform(original);

            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                return;
            }

            var backupPath = existed
                ? $"{fullPath}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak"
                : null;

            if (backupPath is not null)
            {
                File.Copy(fullPath, backupPath, overwrite: false);
            }

            await WriteAtomicallyAsync(fullPath, updated, cancellationToken);

            // Once the new file has been committed, finish or roll back the
            // transaction even if the browser request was cancelled.
            var applyFailure = await TryRestartAndVerifyAsync();

            if (applyFailure is null)
            {
                logger.LogInformation(
                    "TrustTunnel configuration {ConfigurationFile} was updated and the service restarted. Backup: {BackupPath}",
                    fullPath,
                    backupPath);

                return;
            }

            logger.LogError(
                "TrustTunnel failed to apply {ConfigurationFile}: {Failure}. Restoring the previous content.",
                fullPath,
                applyFailure);

            if (existed)
            {
                await WriteAtomicallyAsync(fullPath, original, CancellationToken.None);
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            var rollbackFailure = await TryRestartAndVerifyAsync();

            if (rollbackFailure is not null)
            {
                logger.LogCritical(
                    "TrustTunnel configuration was restored, but the service could not be restarted: {Failure}. Backup: {BackupPath}",
                    rollbackFailure,
                    backupPath);
            }

            var rollbackStatus = rollbackFailure is null
                ? "Предыдущая конфигурация восстановлена, служба запущена."
                : $"Файл восстановлен, но служба не поднялась: {rollbackFailure}.";

            throw new InvalidOperationException(
                $"Новые настройки не применены: {applyFailure}. {rollbackStatus} " +
                (backupPath is null ? string.Empty : $"Резервная копия: {backupPath}"));
        }
        finally
        {
            MutationLock.Release();
        }
    }

    private async Task<string?> TryRestartAndVerifyAsync()
    {
        try
        {
            var restart = await serviceControl.RestartAsync(CancellationToken.None);

            if (!restart.Success)
            {
                return restart.Message;
            }

            var status = await serviceControl.GetStatusAsync(CancellationToken.None);

            if (status.IsActive)
            {
                return null;
            }

            return $"служба после перезапуска находится в состоянии '{status.ActiveState}'";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error while restarting TrustTunnel after a configuration change.");
            return exception.Message;
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("Не удалось определить каталог конфигурации.");

        Directory.CreateDirectory(directory);

        UnixFileMode? previousMode = null;

        if (OperatingSystem.IsLinux() && File.Exists(path))
        {
            previousMode = File.GetUnixFileMode(path);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (previousMode.HasValue)
            {
                File.SetUnixFileMode(temporaryPath, previousMode.Value);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

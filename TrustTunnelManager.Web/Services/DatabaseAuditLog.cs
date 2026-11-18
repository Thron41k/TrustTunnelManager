using Microsoft.EntityFrameworkCore;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;
using TrustTunnelManager.Web.Data;

namespace TrustTunnelManager.Web.Services;

public sealed class DatabaseAuditLog(
    IDbContextFactory<ApplicationDbContext> contextFactory)
    : IAuditLog
{
    public async Task WriteAsync(
        string actor,
        string action,
        string target,
        bool succeeded,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        context.AuditEvents.Add(new AuditEvent
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Actor = Truncate(actor, 256),
            Action = Truncate(action, 128),
            Target = Truncate(target, 512),
            Succeeded = succeeded,
            Details = string.IsNullOrWhiteSpace(details) ? null : Truncate(details, 4000)
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetRecentAsync(
        int count = 200,
        CancellationToken cancellationToken = default)
    {
        var safeCount = Math.Clamp(count, 1, 1000);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AuditEvents
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(safeCount)
            .ToArrayAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

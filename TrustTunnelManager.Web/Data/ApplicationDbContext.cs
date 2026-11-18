using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Web.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Actor).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Action).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Target).HasMaxLength(512).IsRequired();
            entity.Property(item => item.Details).HasMaxLength(4000);
            entity.HasIndex(item => item.CreatedAtUtc);
        });
    }
}

using CitusManager.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Data;

public sealed class ControlDbContext(DbContextOptions<ControlDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<ClusterProfile> Clusters => Set<ClusterProfile>();
    public DbSet<ClusterOperation> Operations => Set<ClusterOperation>();
    public DbSet<OperationStep> OperationSteps => Set<OperationStep>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ClusterProfile>(entity =>
        {
            entity.HasIndex(x => new { x.Host, x.Port, x.Database }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Host).HasMaxLength(255);
            entity.Property(x => x.Database).HasMaxLength(63);
            entity.Property(x => x.Username).HasMaxLength(128);
            entity.Property(x => x.PrometheusBaseUrl).HasMaxLength(500);
        });

        builder.Entity<ClusterOperation>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Status });
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasMany(x => x.Steps).WithOne(x => x.Operation)
                .HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OperationStep>().HasIndex(x => new { x.OperationId, x.Sequence }).IsUnique();
        builder.Entity<AuditEvent>().HasIndex(x => x.OccurredAt);
        builder.Entity<MetricSample>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Name, x.CollectedAt });
            entity.Property(x => x.Name).HasMaxLength(100);
        });
        builder.Entity<AlertRecord>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Fingerprint, x.State });
            entity.Property(x => x.Fingerprint).HasMaxLength(255);
            entity.Property(x => x.Title).HasMaxLength(255);
        });
    }
}

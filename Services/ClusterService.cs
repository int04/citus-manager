using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Security;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public interface IClusterService
{
    Task<IReadOnlyList<ClusterResponse>> GetAllAsync(CancellationToken cancellationToken);
    Task<ClusterResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ClusterResponse> CreateAsync(CreateClusterRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<ClusterInventoryResponse> RefreshAsync(Guid id, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
}

public sealed class ClusterService(
    ControlDbContext db,
    ICitusInspector inspector,
    IClusterSecretProtector secrets) : IClusterService
{
    public async Task<IReadOnlyList<ClusterResponse>> GetAllAsync(CancellationToken cancellationToken) =>
        await db.Clusters.AsNoTracking().OrderBy(x => x.Name).Select(x => Map(x)).ToListAsync(cancellationToken);

    public async Task<ClusterResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return cluster is null ? null : Map(cluster);
    }

    public async Task<ClusterResponse> CreateAsync(
        CreateClusterRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var cluster = new ClusterProfile
        {
            Name = request.Name.Trim(),
            Host = request.Host.Trim(),
            Port = request.Port,
            Database = request.Database.Trim(),
            Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
            ProtectedPassword = string.IsNullOrEmpty(request.Password) ? null : secrets.Protect(request.Password),
            PrometheusBaseUrl = string.IsNullOrWhiteSpace(request.PrometheusBaseUrl) ? null : request.PrometheusBaseUrl.TrimEnd('/'),
            ProtectedPrometheusToken = string.IsNullOrEmpty(request.PrometheusBearerToken) ? null : secrets.Protect(request.PrometheusBearerToken),
            SslMode = request.SslMode
        };

        var inventory = await inspector.CollectAsync(cluster, cancellationToken);
        cluster.PostgreSqlVersion = inventory.Capability.PostgreSqlVersion;
        cluster.CitusVersion = inventory.Capability.CitusVersion;
        cluster.CapabilityJson = JsonSerializer.Serialize(inventory.Capability);
        cluster.LastCheckedAt = inventory.CollectedAt;
        db.Clusters.Add(cluster);
        db.AuditEvents.Add(Audit(actorId, "cluster.create", "cluster", cluster.Id,
            new { cluster.Name, cluster.Host, cluster.Port, cluster.Database, cluster.SslMode }));
        await db.SaveChangesAsync(cancellationToken);
        return Map(cluster);
    }

    public async Task<ClusterInventoryResponse> RefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        try
        {
            var inventory = await inspector.CollectAsync(cluster, cancellationToken);
            cluster.PostgreSqlVersion = inventory.Capability.PostgreSqlVersion;
            cluster.CitusVersion = inventory.Capability.CitusVersion;
            cluster.CapabilityJson = JsonSerializer.Serialize(inventory.Capability);
            cluster.LastCheckedAt = inventory.CollectedAt;
            cluster.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            return inventory;
        }
        catch
        {
            cluster.LastCheckedAt = DateTimeOffset.UtcNow;
            cluster.LastError = "Connection or capability check failed.";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        var active = await db.Operations.AnyAsync(x => x.ClusterId == id &&
            (x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running ||
             x.Status == OperationStatus.Cancelling), cancellationToken);
        if (active)
            throw new InvalidOperationException("Cluster has an active operation.");
        db.Clusters.Remove(cluster);
        db.AuditEvents.Add(Audit(actorId, "cluster.delete-profile", "cluster", id,
            new { cluster.Name, note = "Control-plane profile only; target Citus cluster was not changed." }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ClusterResponse Map(ClusterProfile x) => new(
        x.Id, x.Name, x.Host, x.Port, x.Database, x.Username, x.SslMode,
        !string.IsNullOrWhiteSpace(x.ProtectedPassword), !string.IsNullOrWhiteSpace(x.PrometheusBaseUrl), x.IsEnabled,
        x.PostgreSqlVersion, x.CitusVersion, x.LastCheckedAt, x.LastError);

    internal static AuditEvent Audit(Guid? actorId, string action, string type, object id, object detail) => new()
    {
        ActorId = actorId,
        Action = action,
        ResourceType = type,
        ResourceId = id.ToString(),
        DetailJson = JsonSerializer.Serialize(detail)
    };
}

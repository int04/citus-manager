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
    Task<IReadOnlyList<ClusterQueryEndpointResponse>> GetQueryEndpointsAsync(Guid id, CancellationToken cancellationToken);
    Task<ClusterConnectionTestResponse> TestConnectionAsync(
        TestClusterConnectionRequest request, CancellationToken cancellationToken);
    Task<ClusterResponse> CreateAsync(CreateClusterRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<ClusterInventoryResponse> RefreshAsync(Guid id, CancellationToken cancellationToken, bool force = false);
    Task DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
}

public sealed class ClusterService(
    ControlDbContext db,
    ICitusInspector inspector,
    IClusterSecretProtector secrets,
    IClusterTopologyCache topologyCache) : IClusterService
{
    public async Task<IReadOnlyList<ClusterResponse>> GetAllAsync(CancellationToken cancellationToken) =>
        await db.Clusters.AsNoTracking().OrderBy(x => x.Name).Select(x => Map(x)).ToListAsync(cancellationToken);

    public async Task<ClusterResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return cluster is null ? null : Map(cluster);
    }

    public async Task<IReadOnlyList<ClusterQueryEndpointResponse>> GetQueryEndpointsAsync(
        Guid id, CancellationToken cancellationToken) =>
        await db.ClusterQueryEndpoints.AsNoTracking()
            .Where(x => x.ClusterId == id)
            .OrderBy(x => x.Host).ThenBy(x => x.Port)
            .Select(x => new ClusterQueryEndpointResponse(
                x.Id, x.Host, x.Port, x.IsEnabled, x.Health, x.MetadataSynced,
                x.LastCheckedAt, x.LastError))
            .ToListAsync(cancellationToken);

    public async Task<ClusterConnectionTestResponse> TestConnectionAsync(
        TestClusterConnectionRequest request, CancellationToken cancellationToken)
    {
        var inventory = await inspector.CollectAsync(ToProfile(
            request.Host, request.Port, request.Database, request.Username,
            request.Password, request.SslMode), cancellationToken);
        return new(
            true,
            inventory.Capability.PostgreSqlVersion,
            inventory.Capability.CitusVersion,
            inventory.Capability.Database,
            inventory.Capability.User,
            inventory.Nodes.Count,
            inventory.Tables.Count,
            inventory.CollectedAt);
    }

    public async Task<ClusterResponse> CreateAsync(
        CreateClusterRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var cluster = ToProfile(request.Host, request.Port, request.Database, request.Username,
            request.Password, request.SslMode);
        cluster.Name = request.Name.Trim();
        cluster.PrometheusBaseUrl = string.IsNullOrWhiteSpace(request.PrometheusBaseUrl)
            ? null : request.PrometheusBaseUrl.TrimEnd('/');
        cluster.ProtectedPrometheusToken = string.IsNullOrEmpty(request.PrometheusBearerToken)
            ? null : secrets.Protect(request.PrometheusBearerToken);

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

    public async Task<ClusterInventoryResponse> RefreshAsync(
        Guid id, CancellationToken cancellationToken, bool force = false)
    {
        if (!force && topologyCache.TryGet(id, out var cached)) return cached;
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
            topologyCache.Set(id, inventory);
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
             x.Status == OperationStatus.Cancelling ||
             x.Kind == OperationKind.MigrateControlCoordinator &&
             (x.Status == OperationStatus.AwaitingApproval || x.Status == OperationStatus.RecoveryRequired)),
            cancellationToken);
        if (active)
            throw new InvalidOperationException("Cluster has an active operation.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.RestoreRuns
            .Where(x => x.SourceClusterId == id || x.TargetClusterId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.BackupRuns
            .Where(x => x.ClusterId == id)
            .ExecuteDeleteAsync(cancellationToken);
        db.Clusters.Remove(cluster);
        db.AuditEvents.Add(Audit(actorId, "cluster.delete-profile", "cluster", id,
            new
            {
                cluster.Name,
                note = "Control-plane profile and associated history deleted; target Citus cluster and external backup objects were not changed."
            }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        topologyCache.Remove(id);
    }

    private static ClusterResponse Map(ClusterProfile x) => new(
        x.Id, x.Name, x.Host, x.Port, x.Database, x.Username, x.SslMode,
        !string.IsNullOrWhiteSpace(x.ProtectedPassword), !string.IsNullOrWhiteSpace(x.PrometheusBaseUrl), x.IsEnabled,
        x.PostgreSqlVersion, x.CitusVersion, x.LastCheckedAt, x.LastError);

    private ClusterProfile ToProfile(
        string host, int port, string database, string? username, string? password, ClusterSslMode sslMode) => new()
    {
        Name = host.Trim(),
        Host = host.Trim(),
        Port = port,
        Database = database.Trim(),
        Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
        ProtectedPassword = string.IsNullOrEmpty(password) ? null : secrets.Protect(password),
        SslMode = sslMode
    };

    internal static AuditEvent Audit(Guid? actorId, string action, string type, object id, object detail) => new()
    {
        ActorId = actorId,
        Action = action,
        ResourceType = type,
        ResourceId = id.ToString(),
        DetailJson = JsonSerializer.Serialize(detail)
    };
}

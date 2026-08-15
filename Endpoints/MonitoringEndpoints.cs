using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Services;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Endpoints;

public sealed record MetricSampleResponse(string Name, double Value, string LabelsJson, DateTimeOffset CollectedAt);
public sealed record AlertResponse(long Id, Guid ClusterId, string ClusterName, string Fingerprint, string Title,
    string Detail, AlertSeverity Severity, AlertState State, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

public static class MonitoringEndpoints
{
    public static IEndpointRouteBuilder MapMonitoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/clusters/{clusterId:guid}/metrics", async (
                Guid clusterId, DateTimeOffset? from, int? take, ControlDbContext db, CancellationToken cancellationToken) =>
            {
                var since = from ?? DateTimeOffset.UtcNow.AddHours(-24);
                var limit = Math.Clamp(take ?? 1000, 1, 5000);
                var samples = await db.MetricSamples.AsNoTracking()
                    .Where(x => x.ClusterId == clusterId && x.CollectedAt >= since)
                    .OrderByDescending(x => x.CollectedAt).Take(limit)
                    .Select(x => new MetricSampleResponse(x.Name, x.Value, x.LabelsJson, x.CollectedAt))
                    .ToListAsync(cancellationToken);
                return TypedResults.Ok(samples);
            }).RequireAuthorization().WithTags("Monitoring").WithName("GetClusterMetrics");

        endpoints.MapGet("/api/alerts", async (AlertState? state, ControlDbContext db, CancellationToken cancellationToken) =>
            {
                var query = db.Alerts.AsNoTracking().Include(x => x.Cluster).AsQueryable();
                if (state.HasValue) query = query.Where(x => x.State == state);
                var alerts = await query.OrderByDescending(x => x.LastSeenAt).Take(500)
                    .Select(x => new AlertResponse(x.Id, x.ClusterId, x.Cluster!.Name, x.Fingerprint,
                        x.Title, x.Detail, x.Severity, x.State, x.FirstSeenAt, x.LastSeenAt))
                    .ToListAsync(cancellationToken);
                return TypedResults.Ok(alerts);
            }).RequireAuthorization().WithTags("Monitoring").WithName("GetAlerts");

        endpoints.MapGet("/api/clusters/{clusterId:guid}/activity", async (
                Guid clusterId, ControlDbContext db, ICitusInspector inspector, CancellationToken cancellationToken) =>
            {
                var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
                    ?? throw new KeyNotFoundException("Cluster not found.");
                return TypedResults.Ok(await inspector.GetActivityAsync(cluster, cancellationToken));
            }).RequireAuthorization().WithTags("Monitoring").WithName("GetClusterActivity");
        return endpoints;
    }
}

using System.Text.Json;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public sealed class MonitoringWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<MonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Monitoring:PollingSeconds", 60), 15, 3600));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await CollectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("Monitoring cycle failed ({ErrorType}).", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var inspector = scope.ServiceProvider.GetRequiredService<ICitusInspector>();
        var prometheus = scope.ServiceProvider.GetRequiredService<IPrometheusCollector>();
        var clusters = await db.Clusters.Where(x => x.IsEnabled).ToListAsync(cancellationToken);
        foreach (var cluster in clusters)
        {
            try
            {
                var inventory = await inspector.CollectAsync(cluster, cancellationToken);
                cluster.LastCheckedAt = inventory.CollectedAt;
                cluster.LastError = null;
                AddMetric(db, cluster.Id, "citus.nodes.total", inventory.Nodes.Count);
                AddMetric(db, cluster.Id, "citus.nodes.inactive", inventory.Nodes.Count(x => !x.IsActive));
                AddMetric(db, cluster.Id, "citus.metadata.unsynced", inventory.Nodes.Count(x => x.HasMetadata && !x.MetadataSynced));
                AddMetric(db, cluster.Id, "citus.placements.total", inventory.Nodes.Sum(x => x.PlacementCount));
                AddMetric(db, cluster.Id, "citus.shards.bytes", inventory.Nodes.Sum(x => x.ShardBytes));
                AddMetric(db, cluster.Id, "citus.tables.total", inventory.Tables.Count);
                await SetAlertAsync(db, cluster.Id, "inactive-nodes", "Node không hoạt động",
                    $"{inventory.Nodes.Count(x => !x.IsActive)} node inactive.", AlertSeverity.Critical,
                    inventory.Nodes.Any(x => !x.IsActive), cancellationToken);
                await SetAlertAsync(db, cluster.Id, "metadata-unsynced", "Citus metadata chưa đồng bộ",
                    "Có metadata node báo metadatasynced=false.", AlertSeverity.Critical,
                    inventory.Nodes.Any(x => x.HasMetadata && !x.MetadataSynced), cancellationToken);
                await SetAlertAsync(db, cluster.Id, "collector-failed", "Collector không kết nối được",
                    "SQL collector đã phục hồi.", AlertSeverity.Critical, false, cancellationToken);
                if (!string.IsNullOrWhiteSpace(cluster.PrometheusBaseUrl))
                {
                    try
                    {
                        var hostMetrics = await prometheus.CollectAsync(cluster, cancellationToken);
                        foreach (var metric in hostMetrics) AddMetric(db, cluster.Id, metric.Key, metric.Value);
                        await SetAlertAsync(db, cluster.Id, "prometheus-unavailable", "Prometheus không khả dụng",
                            "Prometheus collector đã phục hồi.", AlertSeverity.Warning, false, cancellationToken);
                    }
                    catch
                    {
                        await SetAlertAsync(db, cluster.Id, "prometheus-unavailable", "Prometheus không khả dụng",
                            "SQL monitoring vẫn hoạt động; kiểm tra Prometheus URL, token và network.",
                            AlertSeverity.Warning, true, cancellationToken);
                    }
                }
            }
            catch
            {
                cluster.LastCheckedAt = DateTimeOffset.UtcNow;
                cluster.LastError = "Monitoring connection failed.";
                await SetAlertAsync(db, cluster.Id, "collector-failed", "Collector không kết nối được",
                    "Kiểm tra coordinator, database, TLS, authentication và network.", AlertSeverity.Critical,
                    true, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        var retentionDays = Math.Clamp(configuration.GetValue("Monitoring:RawRetentionDays", 30), 1, 365);
        await db.MetricSamples.Where(x => x.CollectedAt < DateTimeOffset.UtcNow.AddDays(-retentionDays))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static void AddMetric(ControlDbContext db, Guid clusterId, string name, double value) =>
        db.MetricSamples.Add(new MetricSample { ClusterId = clusterId, Name = name, Value = value });

    private static async Task SetAlertAsync(
        ControlDbContext db, Guid clusterId, string fingerprint, string title, string detail,
        AlertSeverity severity, bool active, CancellationToken cancellationToken)
    {
        var alert = await db.Alerts.SingleOrDefaultAsync(x => x.ClusterId == clusterId &&
            x.Fingerprint == fingerprint && x.State != AlertState.Resolved, cancellationToken);
        if (active)
        {
            if (alert is null)
                db.Alerts.Add(new AlertRecord { ClusterId = clusterId, Fingerprint = fingerprint, Title = title, Detail = detail, Severity = severity });
            else
            {
                alert.LastSeenAt = DateTimeOffset.UtcNow;
                alert.Detail = detail;
            }
        }
        else if (alert is not null)
        {
            alert.State = AlertState.Resolved;
            alert.ResolvedAt = DateTimeOffset.UtcNow;
            alert.LastSeenAt = DateTimeOffset.UtcNow;
        }
    }
}

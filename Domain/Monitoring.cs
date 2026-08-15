namespace CitusManager.Domain;

public sealed class MetricSample
{
    public long Id { get; set; }
    public Guid ClusterId { get; set; }
    public ClusterProfile? Cluster { get; set; }
    public required string Name { get; set; }
    public double Value { get; set; }
    public string LabelsJson { get; set; } = "{}";
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AlertSeverity { Info, Warning, Critical }
public enum AlertState { Open, Acknowledged, Resolved }

public sealed class AlertRecord
{
    public long Id { get; set; }
    public Guid ClusterId { get; set; }
    public ClusterProfile? Cluster { get; set; }
    public required string Fingerprint { get; set; }
    public required string Title { get; set; }
    public required string Detail { get; set; }
    public AlertSeverity Severity { get; set; }
    public AlertState State { get; set; } = AlertState.Open;
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
    public int NotificationAttempts { get; set; }
}

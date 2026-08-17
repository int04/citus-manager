namespace CitusManager.Domain;

public enum QueryEndpointHealth
{
    Unknown,
    Healthy,
    Unhealthy
}

public sealed class ClusterQueryEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public ClusterProfile? Cluster { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 5432;
    public bool IsEnabled { get; set; } = true;
    public QueryEndpointHealth Health { get; set; } = QueryEndpointHealth.Unknown;
    public bool MetadataSynced { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace CitusManager.Domain;

public enum ClusterSslMode
{
    Disable,
    Prefer,
    Require,
    VerifyCa,
    VerifyFull
}

public sealed class ClusterProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "postgres";
    public string? Username { get; set; }
    public string? ProtectedPassword { get; set; }
    public string? PrometheusBaseUrl { get; set; }
    public string? ProtectedPrometheusToken { get; set; }
    public ClusterSslMode SslMode { get; set; } = ClusterSslMode.Prefer;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? PostgreSqlVersion { get; set; }
    public string? CitusVersion { get; set; }
    public string? CapabilityJson { get; set; }
    public string? LastError { get; set; }
    public int Version { get; set; }
}

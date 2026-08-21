using CitusManager.Domain;
using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class CoordinatorMigrationServiceTests
{
    [Fact]
    public void CopyWithEndpoint_preserves_profile_identity_secrets_and_concurrency_version()
    {
        var source = new ClusterProfile
        {
            Name = "cluster",
            Host = "source",
            Port = 5432,
            Database = "app",
            Username = "operator",
            ProtectedPassword = "protected",
            ProtectedPrometheusToken = "metrics-token",
            Version = 7
        };

        var target = CoordinatorMigrationService.CopyWithEndpoint(source, "target", 6432);

        Assert.Equal("target", target.Host);
        Assert.Equal(6432, target.Port);
        Assert.Equal(source.Id, target.Id);
        Assert.Equal(source.Database, target.Database);
        Assert.Equal(source.Username, target.Username);
        Assert.Equal(source.ProtectedPassword, target.ProtectedPassword);
        Assert.Equal(source.ProtectedPrometheusToken, target.ProtectedPrometheusToken);
        Assert.Equal(7, target.Version);
    }
}

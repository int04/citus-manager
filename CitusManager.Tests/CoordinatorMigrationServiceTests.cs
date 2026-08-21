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

    [Fact]
    public void Local_coordinator_relocation_is_metadata_local_and_quotes_host()
    {
        var sql = CoordinatorLogicalMigrationService.BuildLocalCoordinatorRelocationCommand(
            "new'coordinator", 12002);

        Assert.Contains("citus.enable_metadata_sync','off',true", sql, StringComparison.Ordinal);
        Assert.Contains("citus_set_coordinator_host('new''coordinator',12002)", sql, StringComparison.Ordinal);
        Assert.Contains("groupid=0", sql, StringComparison.Ordinal);
        Assert.Contains("count(*)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_cleanup_keeps_database_and_recreates_empty_public_schema()
    {
        var sql = CoordinatorLogicalMigrationService.BuildSourceSchemaPurgeSql("\"app\"");

        Assert.DoesNotContain("DROP DATABASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DROP SCHEMA IF EXISTS public CASCADE", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE SCHEMA public AUTHORIZATION pg_database_owner", sql, StringComparison.Ordinal);
        Assert.Contains("DROP EXTENSION IF EXISTS %I CASCADE", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER DATABASE \"app\" RESET default_transaction_read_only", sql, StringComparison.Ordinal);
    }
}

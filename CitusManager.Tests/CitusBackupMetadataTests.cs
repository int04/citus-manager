using CitusManager.Services;
using System.Text.Json;
using Xunit;

namespace CitusManager.Tests;

public sealed class CitusBackupMetadataTests
{
    [Fact]
    public void Schema_distribute_command_casts_parameter_to_regnamespace()
    {
        Assert.Equal(
            "SELECT citus_schema_distribute($1::regnamespace)",
            CitusBackupMetadataCollector.SchemaDistributeSql);
    }

    [Fact]
    public void Schema_capability_requires_supported_regnamespace_signature()
    {
        var supported = new[] { new CitusBackupCapability("citus_schema_distribute", "schemaname regnamespace") };
        var unsupported = new[] { new CitusBackupCapability("citus_schema_distribute", "schemaname text") };

        Assert.True(CitusBackupMetadataCollector.HasCapability(
            supported, "citus_schema_distribute", "regnamespace"));
        Assert.False(CitusBackupMetadataCollector.HasCapability(
            unsupported, "citus_schema_distribute", "regnamespace"));
    }

    [Fact]
    public void Legacy_backup_topology_can_identify_same_target_coordinator_by_unique_port()
    {
        var source = Topology(
        [
            new("host.docker.internal", 5533, "primary", true, true, true),
            new("host.docker.internal", 7778, "primary", true, true, true),
            new("host.docker.internal", 7779, "primary", true, true, true)
        ]);
        var target = new CitusManager.Domain.ClusterProfile
        {
            Name = "same target", Host = "localhost", Port = 5533, Database = "citusdb"
        };

        var coordinator = CitusBackupMetadataCollector.ResolveSourceCoordinator(source, target);

        Assert.NotNull(coordinator);
        Assert.Equal(5533, coordinator.Port);
        Assert.True(CitusBackupMetadataCollector.CanRecoverSameTargetNodes(source, target));
    }

    [Fact]
    public void New_backup_topology_uses_group_zero_for_coordinator()
    {
        var source = Topology(
        [
            new("coordinator.internal", 5432, "primary", true, false, true, 0, false, "default"),
            new("worker.internal", 5432, "primary", true, false, true, 1, true, "default")
        ]);
        var target = new CitusManager.Domain.ClusterProfile
        {
            Name = "same target", Host = "public-endpoint", Port = 6432, Database = "citusdb"
        };

        var coordinator = CitusBackupMetadataCollector.ResolveSourceCoordinator(source, target);

        Assert.NotNull(coordinator);
        Assert.Equal(0, coordinator.GroupId);
    }

    [Fact]
    public void Ambiguous_legacy_topology_blocks_automatic_node_recovery()
    {
        var source = Topology(
        [
            new("node-a", 5432, "primary", true, false, true),
            new("node-b", 5432, "primary", true, false, true)
        ]);
        var target = new CitusManager.Domain.ClusterProfile
        {
            Name = "same target", Host = "public-endpoint", Port = 5432, Database = "citusdb"
        };

        Assert.Null(CitusBackupMetadataCollector.ResolveSourceCoordinator(source, target));
        Assert.False(CitusBackupMetadataCollector.CanRecoverSameTargetNodes(source, target));
    }

    [Fact]
    public void Legacy_node_json_without_group_fields_remains_readable()
    {
        const string json = """
            {"host":"node","port":5432,"role":"primary","active":true,
             "hasMetadata":true,"metadataSynced":true}
            """;

        var node = JsonSerializer.Deserialize<CitusBackupNode>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(node);
        Assert.Null(node.GroupId);
        Assert.Null(node.ShouldHaveShards);
        Assert.Null(node.NodeCluster);
    }

    private static CitusBackupTopology Topology(IReadOnlyList<CitusBackupNode> nodes) => new(
        1, "citusdb", "PostgreSQL 18.4", "Citus 14.1.0", 0, [], nodes, [], [], [], [],
        "fingerprint", DateTimeOffset.UtcNow);
}

using System.Text;
using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class DistributedMergePlanTests
{
    [Theory]
    [InlineData("14.1-1", 14)]
    [InlineData(" 14.0.0", 14)]
    [InlineData("13.0.5", 13)]
    [InlineData("Citus 14.1.0 on x86_64-pc-linux-gnu, compiled by gcc (Debian 14.2.0-19) 14.2.0, 64-bit", 14)]
    [InlineData("citus 14.2.1", 14)]
    [InlineData("unknown", 0)]
    public void Citus_major_is_parsed_without_assuming_package_suffix(string version, int expected) =>
        Assert.Equal(expected, DatabaseMaintenanceService.ParseCitusMajor(version));

    [Fact]
    public void Generated_staging_name_stays_within_postgresql_identifier_limit()
    {
        var name = DatabaseMaintenanceService.BuildMergeObjectName(
            "đơn_hàng_được_theo_dõi_bằng_tên_rất_dài_và_có_unicode_2026", "cmstg", "0123456789");

        Assert.True(Encoding.UTF8.GetByteCount(name) <= 63);
        Assert.EndsWith("__cmstg_0123456789", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_merge_plan_deserializes_but_has_no_safe_distributed_layout()
    {
        const string json = """
            {"Schema":"public","Table":"events","Partitions":["events_1","events_2"],
             "TargetPartition":"events_merged","CatalogFingerprint":"abc","EstimatedRows":2,
             "Bytes":10,"FromBound":"FROM ('2026-01-01') TO ('2026-02-01')",
             "ToBound":"FROM ('2026-02-01') TO ('2026-03-01')","DatabaseTimeZone":"UTC",
             "Distributed":true,"Warnings":[]}
            """;

        var plan = JsonSerializer.Deserialize<MergePartitionPlan>(json);

        Assert.NotNull(plan);
        Assert.Equal(1, plan.MergePlanVersion);
        Assert.Null(plan.DistributionLayout);
    }

    [Fact]
    public void Reference_merge_plan_keeps_reference_replica_snapshot()
    {
        var layout = new MergeReferenceLayoutPlan(42, "14.1-1", "t", 1, 3, 3, "heap", "placements");
        var plan = new MergePartitionPlan("public", "events", ["events_1", "events_2"], "events_merged",
            "fingerprint", 10, 1024, "FROM ('2026-01-01') TO ('2026-02-01')",
            "FROM ('2026-02-01') TO ('2026-03-01')", "UTC", false, [], 3,
            Sources: [], CopyColumns: ["tenant_id", "created_at"], StagingTable: "events__cmstg_x",
            TableMode: DatabaseTableMode.Reference, ReferenceLayout: layout);

        var restored = JsonSerializer.Deserialize<MergePartitionPlan>(JsonSerializer.Serialize(plan));

        Assert.NotNull(restored);
        Assert.False(restored.Distributed);
        Assert.Equal(DatabaseTableMode.Reference, restored.TableMode);
        Assert.Equal(3, restored.ReferenceLayout?.PlacementCount);
    }
}

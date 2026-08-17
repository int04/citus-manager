using CitusManager.Contracts;
using CitusManager.Domain;
using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class OperationSafetyTests
{
    [Fact]
    public void Remove_requires_exact_typed_worker_host()
    {
        var request = Request(OperationKind.RemoveWorker, "worker-1", "WORKER-1");
        Assert.Throws<ArgumentException>(() => OperationSafety.ValidateRequest(request));
    }

    [Fact]
    public void Impact_operation_requires_external_checks_acknowledgement()
    {
        var request = Request(OperationKind.Rebalance, null, null) with
        {
            ExternalCapacityAndBackupChecksAcknowledged = false
        };
        Assert.Throws<ArgumentException>(() => OperationSafety.ValidateRequest(request));
    }

    [Theory]
    [InlineData(OperationKind.AddWorker, OperationRisk.Write)]
    [InlineData(OperationKind.AddQueryNode, OperationRisk.Write)]
    [InlineData(OperationKind.Rebalance, OperationRisk.Impact)]
    [InlineData(OperationKind.DrainWorker, OperationRisk.Impact)]
    [InlineData(OperationKind.RetireWorker, OperationRisk.Destructive)]
    [InlineData(OperationKind.RemoveWorker, OperationRisk.Destructive)]
    [InlineData(OperationKind.ConvertTable, OperationRisk.Impact)]
    [InlineData(OperationKind.CreatePartitionedTable, OperationRisk.Write)]
    [InlineData(OperationKind.CreateRangePartitions, OperationRisk.Write)]
    [InlineData(OperationKind.MergeRangePartitions, OperationRisk.Impact)]
    [InlineData(OperationKind.InspectTable, OperationRisk.Read)]
    [InlineData(OperationKind.RebuildIndex, OperationRisk.Impact)]
    [InlineData(OperationKind.ChangeTableMode, OperationRisk.Impact)]
    public void Operation_risk_is_fixed(OperationKind kind, OperationRisk expected) =>
        Assert.Equal(expected, OperationSafety.RiskFor(kind));

    [Fact]
    public void Add_worker_with_rebalance_is_impact() =>
        Assert.Equal(OperationRisk.Impact, OperationSafety.RiskFor(OperationKind.AddWorker, true));

    [Fact]
    public void Valid_remove_passes_safety_validation()
    {
        var request = Request(OperationKind.RemoveWorker, "worker-1", "worker-1");
        OperationSafety.ValidateRequest(request);
    }

    [Fact]
    public void Generic_operation_request_rejects_table_conversion()
    {
        var request = Request(OperationKind.ConvertTable, null, null);
        Assert.Throws<ArgumentException>(() => OperationSafety.ValidateRequest(request));
    }

    [Theory]
    [InlineData(OperationKind.AddWorker)]
    [InlineData(OperationKind.AddQueryNode)]
    [InlineData(OperationKind.Rebalance)]
    [InlineData(OperationKind.DrainWorker)]
    [InlineData(OperationKind.RetireWorker)]
    [InlineData(OperationKind.RemoveWorker)]
    [InlineData(OperationKind.ConvertTable)]
    [InlineData(OperationKind.CreatePartitionedTable)]
    [InlineData(OperationKind.CreateRangePartitions)]
    [InlineData(OperationKind.MergeRangePartitions)]
    [InlineData(OperationKind.InspectTable)]
    [InlineData(OperationKind.RebuildIndex)]
    [InlineData(OperationKind.ChangeTableMode)]
    public void Requester_can_queue_any_legacy_operation_they_were_allowed_to_create(OperationKind kind)
    {
        var operation = new ClusterOperation
        {
            Kind = kind,
            PlanJson = "{}",
            PlanHash = "test"
        };

        Assert.True(OperationService.CanRequesterApprove(operation));
    }

    [Fact]
    public void New_operation_defaults_to_approved_queue_state()
    {
        var operation = new ClusterOperation { PlanJson = "{}", PlanHash = "test" };

        Assert.Equal(OperationStatus.Approved, operation.Status);
    }

    private static CreateOperationRequest Request(OperationKind kind, string? host, string? confirmation) => new()
    {
        Kind = kind,
        WorkerHost = host,
        WorkerPort = host is null ? null : 5432,
        TypedConfirmation = confirmation,
        ExternalCapacityAndBackupChecksAcknowledged = true
    };
}

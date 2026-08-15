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
    [InlineData(OperationKind.Rebalance, OperationRisk.Impact)]
    [InlineData(OperationKind.DrainWorker, OperationRisk.Impact)]
    [InlineData(OperationKind.RemoveWorker, OperationRisk.Destructive)]
    public void Operation_risk_is_fixed(OperationKind kind, OperationRisk expected) =>
        Assert.Equal(expected, OperationSafety.RiskFor(kind));

    [Fact]
    public void Valid_remove_passes_safety_validation()
    {
        var request = Request(OperationKind.RemoveWorker, "worker-1", "worker-1");
        OperationSafety.ValidateRequest(request);
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

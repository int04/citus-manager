using CitusManager.Domain;
using CitusManager.Services;
using CitusManager.Contracts;
using Xunit;

namespace CitusManager.Tests;

public sealed class BackupOperationTests
{
    [Fact]
    public void Backup_retry_is_visible_as_operation_with_diagnostics()
    {
        var run = new BackupRun
        {
            ClusterId = Guid.NewGuid(),
            Trigger = BackupTrigger.Manual,
            Status = BackupRunStatus.RetryScheduled,
            PolicySnapshotJson = "{}",
            CurrentPhase = "RetryScheduled",
            SafeError = "Backup artifact writer failed: disk full",
            DiagnosticTail = "pg_dump: error: Broken pipe",
            ProcessExitCode = 1,
            RequestedBy = Guid.NewGuid(),
            Steps =
            [
                new BackupRunStep
                {
                    Sequence = 3,
                    Name = "Dumping",
                    Status = "Failed",
                    SafeError = "Backup artifact writer failed: disk full"
                }
            ]
        };

        var operation = OperationService.MapBackup(run);

        Assert.Equal(OperationKind.Backup, operation.Kind);
        Assert.Equal(OperationStatus.RetryScheduled, operation.Status);
        Assert.Contains("Broken pipe", operation.ResultJson);
        Assert.Equal("Backup artifact writer failed: disk full", operation.Steps.Single().Detail);
    }

    [Fact]
    public void Restore_recovery_required_is_visible_as_destructive_operation()
    {
        var run = new RestoreRun
        {
            BackupRunId = Guid.NewGuid(),
            SourceClusterId = Guid.NewGuid(),
            Status = RestoreRunStatus.RecoveryRequired,
            RequestedBy = Guid.NewGuid(),
            SafeError = "Restore failed after mutation."
        };

        var operation = OperationService.MapRestore(run);

        Assert.Equal(OperationKind.Restore, operation.Kind);
        Assert.Equal(OperationRisk.Destructive, operation.Risk);
        Assert.Equal(OperationStatus.RecoveryRequired, operation.Status);
    }

    [Fact]
    public void Restore_recovery_resolution_requires_ack_note_and_exact_restore_id()
    {
        var id = Guid.NewGuid();
        var request = new ResolveRestoreRecoveryRequest
        {
            ManualRecoveryCompleted = true,
            TypedConfirmation = id.ToString(),
            ResolutionNote = "Validated application rows and Citus topology."
        };

        RestoreService.ValidateRecoveryResolutionRequest(id, request);
        Assert.Throws<ArgumentException>(() => RestoreService.ValidateRecoveryResolutionRequest(id,
            request with { ManualRecoveryCompleted = false }));
        Assert.Throws<ArgumentException>(() => RestoreService.ValidateRecoveryResolutionRequest(id,
            request with { TypedConfirmation = id.ToString().ToUpperInvariant() }));
        Assert.Throws<ArgumentException>(() => RestoreService.ValidateRecoveryResolutionRequest(id,
            request with { ResolutionNote = " " }));
    }

    [Fact]
    public void Resolved_restore_recovery_is_terminal_and_no_longer_requires_recovery()
    {
        var run = new RestoreRun
        {
            BackupRunId = Guid.NewGuid(),
            SourceClusterId = Guid.NewGuid(),
            Status = RestoreRunStatus.RecoveryResolved,
            RequestedBy = Guid.NewGuid(),
            RecoveryResolvedAt = DateTimeOffset.UtcNow,
            RecoveryResolvedBy = Guid.NewGuid(),
            RecoveryResolutionNote = "Manually reconciled."
        };

        var operation = OperationService.MapRestore(run);

        Assert.Equal(OperationStatus.Cancelled, operation.Status);
    }
}

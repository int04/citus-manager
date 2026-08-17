using CitusManager.Domain;
using CitusManager.Services;
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
}

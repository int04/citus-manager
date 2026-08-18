namespace CitusManager.Contracts;

/// <summary>Application update lifecycle state.</summary>
public enum ApplicationUpdateState
{
    Checking,
    Current,
    Available,
    Blocked,
    Queued,
    Pulling,
    BackingUp,
    Restarting,
    Succeeded,
    Failed,
    Unavailable
}

/// <summary>Current application version and update availability.</summary>
public sealed record ApplicationUpdateResponse(
    string CurrentVersion,
    string? LatestVersion,
    ApplicationUpdateState State,
    DateTimeOffset CheckedAt,
    bool ExecutionAvailable,
    string? Message,
    Guid? RequestId = null);


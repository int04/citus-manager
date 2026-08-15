using System.ComponentModel.DataAnnotations;
using CitusManager.Domain;

namespace CitusManager.Contracts;

/// <summary>Payload used to create a reviewed topology operation.</summary>
public sealed record CreateOperationRequest
{
    public required OperationKind Kind { get; init; }
    [MaxLength(255)] public string? WorkerHost { get; init; }
    [Range(1, 65535)] public int? WorkerPort { get; init; }
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [MaxLength(255)] public string? TypedConfirmation { get; init; }
}

/// <summary>Durable operation returned to the UI.</summary>
public sealed record OperationResponse(
    Guid Id,
    Guid ClusterId,
    OperationKind Kind,
    OperationRisk Risk,
    OperationStatus Status,
    string PlanJson,
    string? ResultJson,
    string? SafeError,
    Guid RequestedBy,
    Guid? ApprovedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<OperationStepResponse> Steps);

/// <summary>One checkpoint inside a durable operation.</summary>
public sealed record OperationStepResponse(
    int Sequence,
    string Name,
    string Status,
    string? Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

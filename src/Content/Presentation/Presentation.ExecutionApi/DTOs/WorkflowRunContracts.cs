using Domain.AI.Runs;

namespace Presentation.ExecutionApi.DTOs;

/// <summary>Response to an accepted run request: what to poll, and under what identifier.</summary>
public sealed record StartWorkflowRunResponse
{
    /// <summary>Server-minted identifier of the queued run.</summary>
    public required string JobId { get; init; }

    /// <summary>Where to poll for the run's state.</summary>
    public required string StatusUrl { get; init; }
}

/// <summary>What a cancellation achieved, as the caller sees it.</summary>
/// <remarks>
/// Says whether the run had actually stopped rather than reporting a flat "cancelled". A run that was
/// already executing can only be asked to stop, and a caller told otherwise would start a replacement
/// immediately — against a workflow the first run still holds, which is refused.
/// </remarks>
public sealed record CancelWorkflowRunResponse
{
    /// <summary>Identifier of the run that was cancelled.</summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Whether the run had stopped by the time this was answered. When false it has been signalled and
    /// will stop; read the run's status to confirm.
    /// </summary>
    public required bool Stopped { get; init; }

    /// <summary>How many pending approvals were withdrawn along with the run.</summary>
    public required int WithdrawnApprovals { get; init; }
}

/// <summary>The caller-visible view of a run.</summary>
/// <remarks>
/// A projection rather than the stored record, deliberately. <see cref="RunRecord"/> carries the
/// caller's resolved capability envelope and tenant, which are the host's authorization state and not
/// the caller's business — returning the record directly would publish the exact grant a run holds.
/// </remarks>
public sealed record WorkflowRunResponse
{
    /// <summary>Identifier of the run.</summary>
    public required string JobId { get; init; }

    /// <summary>Identifier of the workflow the run belongs to.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Where the run has got to.</summary>
    public required string Status { get; init; }

    /// <summary>Caller-safe failure reason once the run has failed.</summary>
    public string? Error { get; init; }

    /// <summary>When the run was accepted.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When execution began, if it has.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run reached a terminal state, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Projects a stored run onto the caller-visible shape.</summary>
    /// <param name="record">The stored run.</param>
    public static WorkflowRunResponse FromRecord(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new WorkflowRunResponse
        {
            JobId = record.JobId,
            WorkflowId = record.TargetId,
            Status = record.Status.ToString(),
            Error = record.Error,
            CreatedAt = record.CreatedAt,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt
        };
    }
}

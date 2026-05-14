using Domain.AI.Agents;
using Domain.AI.Governance;

namespace Domain.AI.Orchestration;

/// <summary>
/// Immutable snapshot of a single delegation instance. State transitions produce
/// new records — the JSONL store appends rather than mutates.
/// </summary>
public sealed record DelegationRecord
{
    /// <summary>Unique identifier for this delegation.</summary>
    public required Guid DelegationId { get; init; }

    /// <summary>Parent delegation ID. Null for top-level delegations.</summary>
    public Guid? ParentDelegationId { get; init; }

    /// <summary>Identifies the supervising agent.</summary>
    public required string SupervisorId { get; init; }

    /// <summary>Identifies the agent receiving the delegation.</summary>
    public required string DelegateAgentId { get; init; }

    /// <summary>Built-in agent type profile of the delegate.</summary>
    public required SubagentType DelegateAgentType { get; init; }

    /// <summary>Human-readable description of the delegated task.</summary>
    public required string TaskDescription { get; init; }

    /// <summary>Tool names needed for the task.</summary>
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }

    /// <summary>Extra tools granted for this delegation only. Null if none.</summary>
    public IReadOnlyList<string>? ToolOverrides { get; init; }

    /// <summary>Autonomy tier assigned to this delegation.</summary>
    public required AutonomyLevel AutonomyLevel { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public required DelegationState State { get; init; }

    /// <summary>Nesting depth. 0 for top-level, increments with each nested delegation.</summary>
    public required int DelegationDepth { get; init; }

    /// <summary>When the delegation was created.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the delegation finished. Null while in progress.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Failure reason. Populated when <see cref="State"/> is <see cref="DelegationState.Failed"/>.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Populated when failure is due to an autonomy tier violation.
    /// </summary>
    public AutonomyExceededResult? AutonomyExceeded { get; init; }
}

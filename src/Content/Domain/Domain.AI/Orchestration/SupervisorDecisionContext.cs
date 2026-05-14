using Domain.AI.Governance;

namespace Domain.AI.Orchestration;

/// <summary>
/// All inputs needed by <c>ISupervisorStrategy</c> to select an agent for delegation.
/// Built by the supervisor before calling <c>SelectAgent</c>.
/// </summary>
public sealed record SupervisorDecisionContext
{
    /// <summary>Human-readable description of the task to delegate.</summary>
    public required string TaskDescription { get; init; }

    /// <summary>Tool names needed for the task.</summary>
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }

    /// <summary>Minimum autonomy tier the selected agent must have.</summary>
    public required AutonomyLevel MinimumAutonomyLevel { get; init; }

    /// <summary>Candidate agents available for selection.</summary>
    public required IReadOnlyList<AgentCandidate> AvailableAgents { get; init; }

    /// <summary>Current nesting depth of the delegation chain.</summary>
    public required int CurrentDelegationDepth { get; init; }

    /// <summary>Maximum allowed nesting depth.</summary>
    public required int MaxDelegationDepth { get; init; }
}

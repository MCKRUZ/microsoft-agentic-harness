namespace Domain.AI.Governance;

/// <summary>
/// Captures the structured details when an agent attempts an operation above its trust tier.
/// Embedded in <see cref="Orchestration.DelegationResult"/> and
/// <see cref="Orchestration.DelegationRecord"/> to provide actionable failure information.
/// </summary>
public sealed record AutonomyExceededResult
{
    /// <summary>The tool name or operation the agent tried to invoke.</summary>
    public required string AttemptedAction { get; init; }

    /// <summary>The agent's current autonomy tier.</summary>
    public required AutonomyLevel CurrentLevel { get; init; }

    /// <summary>The minimum tier needed for the attempted action.</summary>
    public required AutonomyLevel RequiredLevel { get; init; }

    /// <summary>Human-readable explanation of why the action was blocked.</summary>
    public required string Reason { get; init; }
}

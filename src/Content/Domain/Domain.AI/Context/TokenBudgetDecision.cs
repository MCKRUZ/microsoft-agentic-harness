namespace Domain.AI.Context;

/// <summary>
/// Represents a token budget continuation decision from the diminishing returns detector.
/// </summary>
public enum TokenBudgetAction
{
    /// <summary>Continue producing output — budget and progress are healthy.</summary>
    Continue,

    /// <summary>Stop producing output — budget exhausted or diminishing returns detected.</summary>
    Stop,

    /// <summary>Continue but warn the agent that budget is running low.</summary>
    Nudge
}

/// <summary>
/// The full assessment of whether an agent should continue producing output.
/// Includes the decision, reason, and tracking metrics.
/// </summary>
public sealed record BudgetAssessment
{
    /// <summary>The recommended action.</summary>
    public required TokenBudgetAction Action { get; init; }

    /// <summary>Human-readable explanation of the decision.</summary>
    public required string Reason { get; init; }

    /// <summary>Number of continuation turns so far.</summary>
    public required int ContinuationCount { get; init; }

    /// <summary>Percentage of total budget consumed (0.0 to 1.0).</summary>
    public required double CompletionPercentage { get; init; }
}

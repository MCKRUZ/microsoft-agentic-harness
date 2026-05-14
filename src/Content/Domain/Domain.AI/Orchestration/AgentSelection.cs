namespace Domain.AI.Orchestration;

/// <summary>
/// The strategy's chosen agent with audit-friendly metadata explaining the selection.
/// </summary>
public sealed record AgentSelection
{
    /// <summary>The candidate selected for delegation.</summary>
    public required AgentCandidate SelectedAgent { get; init; }

    /// <summary>Confidence score from 0.0 (lowest) to 1.0 (highest).</summary>
    public required double ConfidenceScore { get; init; }

    /// <summary>Human-readable explanation for the audit trail.</summary>
    public required string Reasoning { get; init; }
}

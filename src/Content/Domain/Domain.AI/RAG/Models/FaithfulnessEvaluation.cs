namespace Domain.AI.RAG.Models;

/// <summary>
/// Result of evaluating whether an answer is faithful to the retrieved context.
/// </summary>
public sealed record FaithfulnessEvaluation
{
    /// <summary>Whether the answer is considered faithful overall.</summary>
    public required bool IsFaithful { get; init; }

    /// <summary>Faithfulness score (0.0–1.0) where 1.0 means fully grounded.</summary>
    public required double Score { get; init; }

    /// <summary>Claims not supported by the retrieved context.</summary>
    public IReadOnlyList<string> HallucinatedClaims { get; init; } = [];

    /// <summary>Claims supported by the retrieved context.</summary>
    public IReadOnlyList<string> SupportedClaims { get; init; } = [];

    /// <summary>Optional reasoning from the evaluator.</summary>
    public string? Reasoning { get; init; }
}

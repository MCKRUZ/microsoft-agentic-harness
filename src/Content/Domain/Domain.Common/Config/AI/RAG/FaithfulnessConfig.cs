namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for post-assembly answer faithfulness evaluation.
/// </summary>
public sealed class FaithfulnessConfig
{
    /// <summary>Enable post-assembly faithfulness evaluation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Score threshold below which the answer is considered unfaithful.</summary>
    public double HallucinationThreshold { get; set; } = 0.3;

    /// <summary>Whether to require citation support for every claim.</summary>
    public bool RequireCitationSupport { get; set; } = true;
}

namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for multi-hop iterative retrieval.
/// </summary>
public sealed class MultiHopConfig
{
    /// <summary>Enable multi-hop iterative retrieval for complex queries.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of retrieval iterations (hops).</summary>
    public int MaxHops { get; set; } = 3;

    /// <summary>Token budget allocated per individual hop.</summary>
    public int TokenBudgetPerHop { get; set; } = 1024;

    /// <summary>Minimum sufficiency score (0.0–1.0) to consider a sub-query answered.</summary>
    public double MinSufficiencyScore { get; set; } = 0.7;

    /// <summary>Number of results to retrieve per hop.</summary>
    public int TopKPerHop { get; set; } = 5;

    /// <summary>
    /// Maximum number of extra bounded re-retrieval attempts for a single hop
    /// whose sufficiency verdict falls below <see cref="MinSufficiencyScore"/>.
    /// Each extra attempt widens the top-k and refines the sub-query, keeping only
    /// the best-scoring candidate set. Zero (the default) disables re-retrieval so
    /// an insufficient hop advances immediately to the next sub-query, preserving
    /// legacy behavior. Re-retrieval always stops early once the per-run token
    /// budget is exhausted.
    /// </summary>
    public int MaxReRetriesPerHop { get; set; }
}

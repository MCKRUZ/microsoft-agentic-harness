namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for multi-source retrieval orchestration.
/// Controls which sources are enabled, parallelism limits, and per-source timeouts.
/// Bound from <c>AppConfig:AI:Rag:MultiSource</c> in appsettings.json.
/// </summary>
public sealed class MultiSourceConfig
{
    /// <summary>Whether multi-source orchestration is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enabled source names. Valid: "vector", "graph", "web".
    /// Sources not listed are never queried.
    /// </summary>
    public List<string> EnabledSources { get; set; } = ["vector", "graph"];

    /// <summary>Maximum sources to query in parallel.</summary>
    public int MaxParallelSources { get; set; } = 3;

    /// <summary>Per-source timeout. Sources exceeding this are abandoned gracefully.</summary>
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Cost per 1M input tokens in USD for cost estimation.</summary>
    public double CostPerMillionInputTokens { get; set; } = 2.50;

    /// <summary>Cost per 1M output tokens in USD for cost estimation.</summary>
    public double CostPerMillionOutputTokens { get; set; } = 10.00;
}

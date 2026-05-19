namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for complexity-based query routing.
/// Controls thresholds, tier mapping, and cost-awareness.
/// </summary>
public sealed class ComplexityRoutingConfig
{
    /// <summary>Enable complexity-based routing. When false, all queries use the full pipeline.</summary>
    public bool Enabled { get; set; }

    /// <summary>Confidence threshold below which the classifier falls back to Moderate.</summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    /// <summary>TopK for Simple tier (lightweight single-pass). Lower = faster + cheaper.</summary>
    public int SimpleTopK { get; set; } = 5;

    /// <summary>TopK for Moderate tier (full pipeline). Uses existing Retrieval.TopK if not set.</summary>
    public int? ModerateTopK { get; set; }

    /// <summary>TopK for Complex tier (multi-hop, Phase B). Higher for comprehensive retrieval.</summary>
    public int ComplexTopK { get; set; } = 15;

    /// <summary>Skip reranking for Simple tier queries to save latency and cost.</summary>
    public bool SkipRerankForSimple { get; set; } = true;

    /// <summary>Skip CRAG evaluation for Simple tier queries.</summary>
    public bool SkipCragForSimple { get; set; } = true;
}

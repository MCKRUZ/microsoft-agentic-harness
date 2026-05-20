namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for CI/CD retrieval quality gates.
/// Quality gate tests evaluate retrieval against a golden dataset
/// and fail the build if metrics drop below configured thresholds.
/// Bound from <c>AppConfig:AI:Rag:QualityGate</c> in appsettings.json.
/// </summary>
public sealed class QualityGateConfig
{
    /// <summary>Whether quality gate evaluation is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Minimum acceptable context precision (0.0-1.0).</summary>
    public double MinContextPrecision { get; set; } = 0.7;

    /// <summary>Minimum acceptable faithfulness (0.0-1.0).</summary>
    public double MinFaithfulness { get; set; } = 0.8;

    /// <summary>Minimum acceptable overall quality score (0.0-1.0).</summary>
    public double MinOverallScore { get; set; } = 0.7;

    /// <summary>Path to golden dataset JSON file. Null uses embedded default.</summary>
    public string? GoldenDatasetPath { get; set; }
}

namespace Domain.AI.DriftDetection;

/// <summary>
/// Holds the current vs baseline comparison for a single <see cref="DriftDimension"/>.
/// Produced by <c>IDriftScorer</c> during drift evaluation.
/// </summary>
public sealed record DriftDimensionScore
{
    /// <summary>The raw score value from the current evaluation.</summary>
    public required double CurrentValue { get; init; }

    /// <summary>The baseline mean for this dimension.</summary>
    public required double BaselineValue { get; init; }

    /// <summary>The EWMA-smoothed value after incorporating this evaluation.</summary>
    public required double EwmaValue { get; init; }

    /// <summary>
    /// Deviation from baseline in sigma units. Drives severity classification.
    /// Computed as <c>abs(ewma - baselineMean) / sigma</c>.
    /// </summary>
    public required double Deviation { get; init; }
}

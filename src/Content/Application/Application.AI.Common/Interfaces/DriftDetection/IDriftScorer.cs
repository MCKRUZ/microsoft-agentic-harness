using Domain.AI.DriftDetection;
using Domain.Common;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Scores a single dimension's current value against its baseline using a smoothing algorithm.
/// Keyed DI: <c>"ewma"</c> (default).
/// </summary>
public interface IDriftScorer
{
    /// <summary>Computes a dimension score by comparing the current value against the baseline.</summary>
    Task<Result<DriftDimensionScore>> ScoreDimensionAsync(
        DriftDimension dimension, double currentValue, DriftBaseline baseline, CancellationToken ct);
}

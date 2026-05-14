using Domain.AI.DriftDetection;
using Domain.Common.Config.AI.DriftDetection;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Pure function that classifies a sigma deviation into a <see cref="DriftSeverity"/> level.
/// Checks from highest to lowest — at-threshold yields the higher severity.
/// </summary>
public static class DriftSeverityClassifier
{
    /// <summary>
    /// Classifies the given sigma deviation against the configured thresholds.
    /// </summary>
    /// <param name="deviation">Deviation from baseline in sigma units (non-negative).</param>
    /// <param name="config">Drift detection configuration containing threshold values.</param>
    /// <returns>The appropriate <see cref="DriftSeverity"/> for the deviation level.</returns>
    public static DriftSeverity Classify(double deviation, DriftDetectionConfig config)
    {
        if (deviation >= config.EscalateThresholdSigma) return DriftSeverity.Escalate;
        if (deviation >= config.AlertThresholdSigma) return DriftSeverity.Alert;
        if (deviation >= config.WarnThresholdSigma) return DriftSeverity.Warn;
        return DriftSeverity.None;
    }
}

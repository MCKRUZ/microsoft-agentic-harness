using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for the learnings subsystem.
/// Recorded by command handlers on remember, recall, forget, improve, and prune operations.
/// </summary>
/// <remarks>
/// Pruned counter is recorded by <c>LearningPruningBackgroundService</c> (Section 11).
/// All other counters are recorded by their respective CQRS handlers (Section 13).
/// </remarks>
public static class LearningsMetrics
{
    /// <summary>Learnings captured. Tags: category, scope.</summary>
    public static Counter<long> Remembered { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Remembered, "{learning}", "Learnings remembered");

    /// <summary>Learnings recalled.</summary>
    public static Counter<long> Recalled { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Recalled, "{learning}", "Learnings recalled");

    /// <summary>Learnings soft-deleted.</summary>
    public static Counter<long> Forgotten { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Forgotten, "{learning}", "Learnings forgotten");

    /// <summary>Learnings feedback-improved.</summary>
    public static Counter<long> Improved { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Improved, "{learning}", "Learnings improved");

    /// <summary>Expired learnings pruned by background service.</summary>
    public static Counter<long> Pruned { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Pruned, "{learning}", "Learnings pruned");

    /// <summary>Recall pipeline duration histogram.</summary>
    public static Histogram<double> RecallDurationMs { get; } =
        AppInstrument.Meter.CreateHistogram<double>(LearningConventions.RecallDurationMs, "ms", "Recall query duration");
}

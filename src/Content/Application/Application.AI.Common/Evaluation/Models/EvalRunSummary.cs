using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// Compact projection of a persisted eval run, suitable for the dashboard's
/// run-history list view. Drops the heavyweight per-case <c>Results</c> array
/// from <see cref="EvalRunReport"/> so list pages don't pay for full payloads.
/// </summary>
/// <remarks>
/// Per-case detail is fetched lazily on drill-in via
/// <see cref="IEvalRunStore.GetRunDetailAsync"/>.
/// </remarks>
public sealed record EvalRunSummary
{
    /// <summary>The run's natural identifier (stable across ingest/replay).</summary>
    public required string RunId { get; init; }

    /// <summary>UTC timestamp when the run started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC timestamp when the run completed.</summary>
    public required DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Wall-clock duration of the run.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Number of cases with overall <see cref="Verdict.Pass"/>.</summary>
    public int PassedCount { get; init; }

    /// <summary>Number of cases with overall <see cref="Verdict.Fail"/>.</summary>
    public int FailedCount { get; init; }

    /// <summary>Number of cases with overall <see cref="Verdict.Warn"/>.</summary>
    public int WarnedCount { get; init; }

    /// <summary>Number of cases that errored during execution.</summary>
    public int ErroredCount { get; init; }

    /// <summary>Cumulative cost in USD across all cases, repeats, and metrics.</summary>
    public decimal TotalCostUsd { get; init; }

    /// <summary>Repeats setting used for this run.</summary>
    public int Repeats { get; init; } = 1;

    /// <summary>Run-level verdict (worst of all case verdicts vs. fail-rate threshold).</summary>
    public required Verdict OverallVerdict { get; init; }

    /// <summary>UTC timestamp the dashboard received this run.</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>
    /// Pass rate across non-errored cases, 0.0–1.0. Mirrors
    /// <see cref="EvalRunReport.PassRate"/>.
    /// </summary>
    public double PassRate => (PassedCount + FailedCount + WarnedCount) == 0
        ? 0.0
        : (double)PassedCount / (PassedCount + FailedCount + WarnedCount);
}

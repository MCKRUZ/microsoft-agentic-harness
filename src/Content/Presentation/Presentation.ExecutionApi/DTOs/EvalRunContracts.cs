using Application.Core.CQRS.Evaluation.Runs;
using Domain.AI.Evaluation;

namespace Presentation.ExecutionApi.DTOs;

/// <summary>The datasets this host will evaluate.</summary>
/// <remarks>
/// Published so a caller can discover what to name without guessing. Safe to expose because the list is
/// operator-curated by construction — it is the top level of the configured dataset roots, so it
/// discloses nothing the operator did not deliberately place there. A host with no roots configured
/// publishes nothing rather than enumerating its filesystem.
/// </remarks>
public sealed record EvalDatasetsResponse
{
    /// <summary>Dataset names, in a stable order.</summary>
    public required IReadOnlyList<string> Datasets { get; init; }
}

/// <summary>A request to evaluate one or more named datasets.</summary>
/// <remarks>
/// <para>
/// <strong>There is no path field, and that is the security property.</strong> Datasets are named; the
/// host resolves a name inside its configured roots. A path on the wire would make every request a
/// filesystem reference with one guard between it and an arbitrary read. A name cannot express
/// "outside the roots" at all, so the dangerous request is unrepresentable rather than rejected.
/// </para>
/// <para>
/// <strong>The option surface is narrower than <c>EvalRunOptions</c>, deliberately.</strong>
/// <c>InvocationOverrides</c> and <c>ForceDeterministic</c> are not accepted here: the first is a free
/// dictionary that flows into model invocation — caller-controlled model configuration, which is the
/// CLI's business and not a remote caller's — and the second exists for local replay. Both remain
/// available to in-process dispatchers of <c>RunEvalSuiteCommand</c>. Omitting them from the wire means
/// they cannot be sent, which is a stronger statement than validating them away.
/// </para>
/// </remarks>
public sealed record StartEvalRunRequest
{
    /// <summary>Names of the datasets to evaluate, as <see cref="EvalDatasetsResponse"/> reports them.</summary>
    public IReadOnlyList<string> Datasets { get; init; } = [];

    /// <summary>
    /// How many times each case is re-invoked, with median-across-repeats aggregation. Bounded by
    /// <c>AppConfig:AI:Evaluation:MaxRepeats</c>.
    /// </summary>
    public int Repeats { get; init; } = 1;

    /// <summary>
    /// How many cases run at once. Bounded by <c>AppConfig:AI:Evaluation:MaxParallelism</c>.
    /// </summary>
    public int Parallelism { get; init; } = 1;

    /// <summary>Optional tag filter; when non-empty, only cases carrying one of these tags run.</summary>
    public IReadOnlyList<string>? TagFilter { get; init; }

    /// <summary>
    /// The fraction of failed cases the run tolerates before its overall verdict is Fail. 0.0 is
    /// strict: any failure fails the run.
    /// </summary>
    public double FailRateThreshold { get; init; }
}

/// <summary>Response to an accepted evaluation run: what to poll, and under what identifier.</summary>
public sealed record StartEvalRunResponse
{
    /// <summary>Server-minted identifier of the queued run.</summary>
    public required string JobId { get; init; }

    /// <summary>Where to poll for the run's state and, once it finishes, its report.</summary>
    public required string StatusUrl { get; init; }
}

/// <summary>What a cancellation achieved, as the caller sees it.</summary>
public sealed record CancelEvalRunResponse
{
    /// <summary>Identifier of the run that was cancelled.</summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Whether the run had stopped by the time this was answered. False means it was already executing
    /// and will run to completion; an evaluation in flight cannot be interrupted.
    /// </summary>
    public required bool Stopped { get; init; }
}

/// <summary>The caller-visible view of an evaluation run.</summary>
/// <remarks>
/// A projection rather than the stored record. <c>RunRecord</c> carries the caller's resolved capability
/// envelope and tenant — the host's authorization state, not the caller's business — so returning it
/// directly would publish the exact grant a run holds.
/// </remarks>
public sealed record EvalRunResponse
{
    /// <summary>Identifier of the run.</summary>
    public required string JobId { get; init; }

    /// <summary>Where the run has got to.</summary>
    public required string Status { get; init; }

    /// <summary>The datasets the run was asked to evaluate.</summary>
    public required IReadOnlyList<string> Datasets { get; init; }

    /// <summary>Caller-safe failure reason once the run has failed.</summary>
    public string? Error { get; init; }

    /// <summary>When the run was accepted.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When execution began, if it has.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run reached a terminal state, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The outcome, once the run has produced one.</summary>
    public EvalRunSummaryResponse? Report { get; init; }

    /// <summary>Projects a run and its report onto the caller-visible shape.</summary>
    /// <param name="view">The run, its datasets, and its report if it has one.</param>
    public static EvalRunResponse FromView(EvalRunView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new EvalRunResponse
        {
            JobId = view.Run.JobId,
            Status = view.Run.Status.ToString(),
            Datasets = view.DatasetNames,
            Error = view.Run.Error,
            CreatedAt = view.Run.CreatedAt,
            StartedAt = view.Run.StartedAt,
            CompletedAt = view.Run.CompletedAt,
            Report = view.Report is null ? null : EvalRunSummaryResponse.FromReport(view.Report)
        };
    }
}

/// <summary>The caller-visible summary of an evaluation report.</summary>
/// <remarks>
/// <para>
/// <strong>Counts and a verdict, not the per-case results.</strong> <c>EvalRunReport.Results</c> holds
/// every case's input and the agent's full output — which for an evaluation of a harness means model
/// responses, tool traces, and whatever the dataset's inputs contained. Returning those on a poll would
/// make a status endpoint the largest response the API serves, and would put agent output on a path
/// nothing else on this surface exposes. A caller who needs the transcripts has the reporters the
/// framework already writes; a caller polling a job needs to know how it went.
/// </para>
/// <para>
/// Cost is included because it is the number a caller most needs after triggering spend on someone
/// else's credentials, and it is an aggregate that reveals nothing about content.
/// </para>
/// </remarks>
public sealed record EvalRunSummaryResponse
{
    /// <summary>The framework's identifier for the evaluation, as it appears in written reports.</summary>
    public required string RunId { get; init; }

    /// <summary>The run's overall verdict.</summary>
    public required string Verdict { get; init; }

    /// <summary>Cases whose overall verdict was Pass.</summary>
    public required int Passed { get; init; }

    /// <summary>Cases whose overall verdict was Fail.</summary>
    public required int Failed { get; init; }

    /// <summary>Cases whose overall verdict was Warn.</summary>
    public required int Warned { get; init; }

    /// <summary>Cases that could not be scored because execution errored.</summary>
    public required int Errored { get; init; }

    /// <summary>Passed as a fraction of scored cases, 0.0–1.0.</summary>
    public required double PassRate { get; init; }

    /// <summary>How long the evaluation took.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Cumulative cost in USD across every case, repeat, and metric.</summary>
    public required decimal TotalCostUsd { get; init; }

    /// <summary>Advisory notes the run produced, such as cost caveats. Never a failure reason.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Projects a report onto the caller-visible summary.</summary>
    /// <param name="report">The report the run produced.</param>
    public static EvalRunSummaryResponse FromReport(EvalRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new EvalRunSummaryResponse
        {
            RunId = report.RunId,
            Verdict = report.OverallVerdict.ToString(),
            Passed = report.PassedCount,
            Failed = report.FailedCount,
            Warned = report.WarnedCount,
            Errored = report.ErroredCount,
            PassRate = report.PassRate,
            Duration = report.Duration,
            TotalCostUsd = report.TotalCostUsd,
            Warnings = report.Warnings
        };
    }
}

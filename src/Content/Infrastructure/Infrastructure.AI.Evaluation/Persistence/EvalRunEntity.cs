namespace Infrastructure.AI.Evaluation.Persistence;

/// <summary>
/// EF-mapped row for one ingested <c>EvalRunReport</c>. Header table — per-case
/// results live in <see cref="EvalCaseResultEntity"/> and per-metric scores in
/// <see cref="EvalMetricScoreEntity"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunId"/> is the natural key and carries a unique index so re-ingest of
/// the same report is a no-op at the store boundary (idempotency).
/// </para>
/// <para>
/// <see cref="DatasetsJson"/> stores the dataset metadata (name/version/path/description
/// without the case bodies) as a JSON blob — datasets are immutable artifacts that
/// only need to be displayed verbatim, never queried by their fields.
/// </para>
/// </remarks>
public sealed class EvalRunEntity
{
    /// <summary>Autoincrement surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>Natural identifier from the report. Unique across rows.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>UTC timestamp the run started.</summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>UTC timestamp the run completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>Wall-clock duration in 100-ns ticks (mirrors <see cref="TimeSpan.Ticks"/>).</summary>
    public long DurationTicks { get; set; }

    /// <summary>Count of cases with overall Pass verdict.</summary>
    public int PassedCount { get; set; }

    /// <summary>Count of cases with overall Fail verdict.</summary>
    public int FailedCount { get; set; }

    /// <summary>Count of cases with overall Warn verdict.</summary>
    public int WarnedCount { get; set; }

    /// <summary>Count of cases that errored during execution.</summary>
    public int ErroredCount { get; set; }

    /// <summary>Cumulative cost in USD across all cases, repeats, and metrics.</summary>
    public decimal TotalCostUsd { get; set; }

    /// <summary>Repeats setting used for this run.</summary>
    public int Repeats { get; set; } = 1;

    /// <summary>Run-level verdict stored as <c>Verdict</c> enum integer (Pass=0, Fail=1, Warn=2).</summary>
    public int OverallVerdict { get; set; }

    /// <summary>Dataset metadata serialized as JSON (name, version, description, source-path; no case bodies).</summary>
    public string DatasetsJson { get; set; } = "[]";

    /// <summary>Non-failure warnings surfaced by the run handler, serialized as a JSON string array.</summary>
    public string WarningsJson { get; set; } = "[]";

    /// <summary>UTC timestamp the dashboard ingested this row. Distinct from <see cref="CompletedAtUtc"/>.</summary>
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

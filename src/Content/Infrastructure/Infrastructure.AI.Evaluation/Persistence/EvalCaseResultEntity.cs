namespace Infrastructure.AI.Evaluation.Persistence;

/// <summary>
/// EF-mapped row for one case result within an ingested run. Foreign key by
/// <see cref="RunId"/> (the natural key on <see cref="EvalRunEntity"/>) so
/// queries can join purely on the public identifier without surfacing the
/// surrogate <c>EvalRunEntity.Id</c>.
/// </summary>
/// <remarks>
/// <para>
/// Dictionaries and lists from the source <c>EvalCase</c> (Tags, InvocationOverrides,
/// MetricSpecs) plus the per-repeat outputs and per-repeat scores are stored as
/// JSON blobs — they are read-only forensic data that is never queried by content.
/// </para>
/// <para>
/// Per-metric aggregated scores get broken out into a dedicated
/// <see cref="EvalMetricScoreEntity"/> table so Step 5.4.3's prompt-version
/// comparison query can <c>AVG(score) GROUP BY prompt_version</c> efficiently
/// in SQL.
/// </para>
/// </remarks>
public sealed class EvalCaseResultEntity
{
    /// <summary>Autoincrement surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>Foreign key to <see cref="EvalRunEntity.RunId"/>. Indexed.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Dataset the case belonged to (denormalised so per-case rows can be queried without joining datasets).</summary>
    public string DatasetName { get; set; } = string.Empty;

    /// <summary>Stable case identifier from the dataset YAML.</summary>
    public string CaseId { get; set; } = string.Empty;

    /// <summary>Case input text. Stored verbatim for replay.</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>Optional reference output the case was scored against.</summary>
    public string? ExpectedOutput { get; set; }

    /// <summary>Optional retrieved context (RAG metrics).</summary>
    public string? RetrievedContext { get; set; }

    /// <summary>Case tags serialized as JSON string array.</summary>
    public string TagsJson { get; set; } = "[]";

    /// <summary>Case invocation overrides serialized as JSON object.</summary>
    public string InvocationOverridesJson { get; set; } = "{}";

    /// <summary>MetricSpec definitions for this case, serialized as JSON.</summary>
    public string MetricSpecsJson { get; set; } = "[]";

    /// <summary>Harness output per repeat, serialized as JSON string array.</summary>
    public string OutputPerRepeatJson { get; set; } = "[]";

    /// <summary>Per-repeat metric scores serialized as JSON (outer array = repeats; inner = metrics).</summary>
    public string ScoresPerRepeatJson { get; set; } = "[]";

    /// <summary>Case-level verdict as <c>Verdict</c> enum integer.</summary>
    public int Verdict { get; set; }

    /// <summary>Cumulative cost in USD for this case.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>Wall-clock duration for this case in 100-ns ticks.</summary>
    public long DurationTicks { get; set; }

    /// <summary>Optional error string set when the case failed to execute.</summary>
    public string? Error { get; set; }
}

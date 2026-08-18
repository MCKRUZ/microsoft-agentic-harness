using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation.Interfaces;

/// <summary>
/// Scores a single evaluation case's output. Implementations are registered as
/// keyed services so cases can reference them by <see cref="Key"/> in YAML/JSON.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be safe to invoke concurrently — the runner may score
/// many cases in parallel against the same metric instance.
/// </para>
/// <para>
/// Implementations should fail soft: when a metric cannot produce a confident score
/// (e.g. an LLM judge returns malformed JSON), return a <see cref="MetricScore"/> with
/// <see cref="Verdict.Warn"/> rather than throwing. Exceptions bubble to the runner
/// and mark the case as Errored, which is a heavier failure mode than Warn.
/// </para>
/// </remarks>
public interface IEvalMetric
{
    /// <summary>
    /// Empty set shared by every metric that reads no <see cref="MetricSpec.Parameters"/> —
    /// avoids each such metric allocating its own empty <see cref="HashSet{T}"/> instance.
    /// </summary>
    private static readonly IReadOnlySet<string> NoRecognizedParameters = new HashSet<string>();

    /// <summary>The stable string key by which this metric is referenced from cases (e.g. "exact_match").</summary>
    string Key { get; }

    /// <summary>
    /// The <see cref="MetricSpec.Parameters"/> keys this metric actually reads. Empty by default —
    /// only a metric that reads case-author-supplied parameters needs to override this.
    /// </summary>
    /// <remarks>
    /// <see cref="Application.AI.Common.Evaluation.MetricSpecExtensions"/>'s accessors are
    /// deliberately fail-soft: a missing or unparseable parameter falls back to a default rather
    /// than throwing, so a bad case must not take down an eval run. The cost is that a typo'd key
    /// is architecturally indistinguishable from an absent one at score time — both hit the same
    /// silent-default path (#423, first surfaced by #410's six silently-no-op'd eval cases). This
    /// property exists so that distinction can be made at dataset-load time instead: a validation
    /// pass can compare a case's declared <see cref="MetricSpec.Parameters"/> keys against the
    /// resolved metric's own declared set and flag anything neither side recognizes, without
    /// needing to inspect <see cref="ScoreAsync"/>'s behavior to find out what it actually reads.
    /// </remarks>
    IReadOnlySet<string> RecognizedParameterKeys => NoRecognizedParameters;

    /// <summary>
    /// Scores the given case's output.
    /// </summary>
    /// <param name="case">The case being evaluated. Provides expected output, context, etc.</param>
    /// <param name="output">The harness output for this case.</param>
    /// <param name="spec">The metric specification from the case (threshold, parameters).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The score and verdict. Never null.</returns>
    Task<MetricScore> ScoreAsync(
        EvalCase @case,
        AgentInvocationResult output,
        MetricSpec spec,
        CancellationToken cancellationToken);
}

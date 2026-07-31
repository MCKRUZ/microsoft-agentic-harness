using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// One evaluation run as the caller asked for it: which datasets, and under what options.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Datasets are named, never pathed.</strong> The wire contract carries names that mean
/// something inside the host's configured dataset roots, and the mapping from a name to a file happens
/// server-side. A path on the wire would make every caller's request a filesystem reference, and the
/// only thing between that and an arbitrary read would be a guard remembering to refuse — a check that
/// holds until the day something bypasses it. A name cannot express "outside the roots" at all, so the
/// dangerous request becomes unrepresentable rather than rejected.
/// </para>
/// <para>
/// <em>The guard still runs underneath.</em> <c>IEvalDatasetPathGuard</c> confines whatever the
/// resolver produces, so a future caller that reintroduces a path is still contained. Defence in
/// depth: the wire shape makes the attack unsayable, the guard makes it ineffective.
/// </para>
/// <para>
/// Separate from the run substrate's <c>RunRecord</c> because that record is deliberately
/// kind-agnostic — it carries identity, ownership and lifecycle, and what a given <c>RunKind</c> needs
/// beyond that belongs to that kind's own record.
/// </para>
/// </remarks>
public sealed record EvalRunSubmission
{
    /// <summary>The run this submission is the input for. Also the run record's <c>TargetId</c>.</summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Names of the datasets to evaluate, as they are known inside the host's configured roots.
    /// </summary>
    public required IReadOnlyList<string> DatasetNames { get; init; }

    /// <summary>The options the run executes under, already bounded by the configured ceilings.</summary>
    public required EvalRunOptions Options { get; init; }

    /// <summary>
    /// The report, once the run has produced one.
    /// </summary>
    /// <remarks>
    /// Held here rather than only logged, so a caller polling the run can read the outcome it paid
    /// for. A run whose report exists nowhere the caller can reach has spent real model budget to
    /// produce a log line.
    /// </remarks>
    public EvalRunReport? Report { get; init; }
}

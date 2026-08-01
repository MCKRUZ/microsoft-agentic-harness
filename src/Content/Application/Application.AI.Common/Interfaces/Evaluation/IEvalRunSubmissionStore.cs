using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Interfaces.Evaluation;

/// <summary>
/// Holds what an evaluation run needs beyond the kind-agnostic run record: which datasets it named,
/// and the report it produced.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not on <c>RunRecord</c>.</strong> That record is deliberately kind-agnostic —
/// identity, ownership, lifecycle, timing — so the queue, the dispatcher, single-arming and expiry are
/// written once for every kind of work. Dataset names and an evaluation report are true of exactly one
/// kind, and putting them there would make the next kind's inputs the third set of mostly-null columns.
/// </para>
/// <para>
/// <strong>Ownership is not checked here, and that is deliberate.</strong> Entries are keyed by job id
/// and reached only after <c>IRunJobStore.Get</c> has already answered for the caller — a run that
/// belongs to someone else resolves to nothing there, so nothing asks this store about it. Adding a
/// second identity check would be an unreachable one, which reads as protection that is never
/// exercised. The rule this depends on is that <em>no</em> caller-facing path may reach this store
/// without a scoped read first.
/// </para>
/// <para>
/// Implements <see cref="IRunReclaimListener"/> rather than leaving cleanup to a caller: a report is
/// held per run, so an implementation that never dropped one would grow for the life of the process.
/// Requiring it on the interface makes that a decision every implementation has to make rather than
/// one an author can omit.
/// </para>
/// </remarks>
public interface IEvalRunSubmissionStore : IRunReclaimListener
{
    /// <summary>Records a submission for a run that has just been accepted.</summary>
    /// <param name="submission">The submission, keyed by its <see cref="EvalRunSubmission.JobId"/>.</param>
    /// <exception cref="InvalidOperationException">A submission for that job id is already held.</exception>
    void Add(EvalRunSubmission submission);

    /// <summary>
    /// Reads a submission, or <see langword="null"/> when none is held for that run.
    /// </summary>
    /// <remarks>
    /// A missing entry for a run the caller could read is possible and is not an error: the record and
    /// the submission are reclaimed on the same sweep, but nothing makes the two writes atomic, and a
    /// caller polling in that window is owed the run's status rather than a failure.
    /// </remarks>
    /// <param name="jobId">The run to read.</param>
    EvalRunSubmission? Get(string jobId);

    /// <summary>
    /// Attaches the report a finished run produced, reporting whether a submission was there to attach
    /// it to.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Add"/> because the report does not exist when the run is accepted, and
    /// the run is accepted long before anything executes. A run whose report is held nowhere the caller
    /// can reach has spent real model budget to produce a log line.
    /// </remarks>
    /// <param name="jobId">The run that produced the report.</param>
    /// <param name="report">The report to attach.</param>
    bool AttachReport(string jobId, EvalRunReport report);
}

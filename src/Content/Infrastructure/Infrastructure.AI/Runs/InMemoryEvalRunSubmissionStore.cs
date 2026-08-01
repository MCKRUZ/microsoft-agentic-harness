using System.Collections.Concurrent;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Evaluation;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Process-local <see cref="IEvalRunSubmissionStore"/>. One entry per accepted evaluation run, dropped
/// when that run's record is reclaimed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Process-local, and that is a real limit — the same one the run store carries.</strong> A
/// submission is visible only to the instance that accepted it, so a multi-instance deployment must
/// route a caller's polls back to that instance or replace both stores together. Replacing one alone
/// would be worse than replacing neither: a caller would reach a shared run record on any instance and
/// find its report missing on all but one.
/// </para>
/// <para>
/// <strong>Lifetime is borrowed, never independent.</strong> Entries go when
/// <see cref="OnRunsReclaimed"/> says the run's record has gone. Giving this store a retention clock of
/// its own would put two schedules in charge of one run's memory, and the two would disagree: the
/// shorter one silently strips the report off a run the caller can still read.
/// </para>
/// </remarks>
public sealed class InMemoryEvalRunSubmissionStore : IEvalRunSubmissionStore
{
    private readonly ConcurrentDictionary<string, EvalRunSubmission> _submissions =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Add(EvalRunSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrEmpty(submission.JobId);

        // Throws rather than overwrites, matching IRunJobStore.TryCreate on the same identifier. A
        // second submission under one job id means two runs are sharing an entry, and the one that
        // lost would report the other's datasets and the other's report to its caller.
        if (!_submissions.TryAdd(submission.JobId, submission))
            throw new InvalidOperationException($"A submission for job id '{submission.JobId}' already exists.");
    }

    /// <inheritdoc />
    public EvalRunSubmission? Get(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        return _submissions.GetValueOrDefault(jobId);
    }

    /// <inheritdoc />
    public bool AttachReport(string jobId, EvalRunReport report)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(report);

        // Compare-and-swap rather than read-modify-write: a plain overwrite would clobber a concurrent
        // sweep's removal and resurrect an entry nothing will ever reclaim again.
        //
        // Not a retry loop, deliberately. One run produces one report, so this is the only writer to
        // this key; the only competing write is the sweep's removal, and losing to that means the entry
        // is gone rather than changed. Retrying would re-add what the sweep just dropped.
        return _submissions.TryGetValue(jobId, out var existing)
            && _submissions.TryUpdate(jobId, existing with { Report = report }, existing);
    }

    /// <inheritdoc />
    public void OnRunsReclaimed(IReadOnlyList<string> jobIds)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        foreach (var jobId in jobIds)
            _submissions.TryRemove(jobId, out _);
    }
}

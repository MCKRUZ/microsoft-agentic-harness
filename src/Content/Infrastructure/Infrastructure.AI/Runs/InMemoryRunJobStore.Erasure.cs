using Application.AI.Common.Interfaces.Compliance;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Runs;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Runs;

/// <summary>Right-to-erasure (#926): the person's finished runs.</summary>
public sealed partial class InMemoryRunJobStore : IPersonalDataEraser
{
    /// <inheritdoc/>
    public string ErasureStoreName => "workflow and evaluation runs";

    /// <inheritdoc/>
    /// <remarks>Nothing else is found through a run record: a planned task's ids come from the plan store.</remarks>
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.Owner;

    /// <inheritdoc />
    public string KeptRunsStoreName => "workflow and evaluation runs still held";

    /// <inheritdoc />
    public string KeptRunsReason =>
        "a run record of the person's was kept: a workflow or evaluation run that was still executing when erasure stopped waiting for it, one that ended or started while erasure ran, or one whose run-scoped state could not be released; run erasure again once the person's runs have ended";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Matches the owner through <see cref="ScopeIdentity.AreSame"/>, the comparison every read here uses: a run records
    /// its owner as issued, and one a schedule started before #907 in the lower-cased form. Every tenant, because erasure
    /// is about the person, not the tenant the run was started under.
    /// </para>
    /// <para>
    /// <strong>Only finished runs, the same rule the retention sweep follows.</strong> A run still queued, running or
    /// parked on an approval is kept. Removing a running run's record would unlock its target while the dispatcher is
    /// still executing it, so a second run could share its plan; removing a parked one would strand the approvals it waits
    /// on, which only the cancel path withdraws; and a queued one would be a run nothing ever finishes. Erasure therefore
    /// cancels the person's live runs through that path before any store is asked (#992), and stops their planned tasks
    /// (#907), so by now a queued or parked run is cancelled and is removed here with the rest. What is still kept (a run
    /// that did not stop in time, or one started meanwhile) is counted by <see cref="CountRunsKeptAsync"/>, and the
    /// receipt says to run erasure again.
    /// </para>
    /// <para>
    /// <strong>Every other holder releases first, and the records go only when all of them did.</strong> Progress
    /// bookkeeping and an evaluation's report are kept under a run's id, and the record is the only thing that leads back
    /// to them. Removing the records first would leave a holder that failed with nothing a retry could find. Instead a
    /// failure keeps every record: <see cref="CountRunsKeptAsync"/> still counts them, so the receipt says to run erasure
    /// again, and the retention sweep tells the holders again when the records expire. Nothing is thrown, because a
    /// failure here must not hold back the person's conversations and plans, which nothing here leads to.
    /// </para>
    /// </remarks>
    public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var finished = OwnedBy(subject.OwnerId).Where(r => r.IsTerminal).Select(r => r.JobId).ToList();
        if (!RunReclaimNotifier.Notify(_reclaimListeners, finished, _logger))
            return Task.FromResult(0);

        var removed = 0;
        foreach (var jobId in finished)
        {
            if (!_entries.TryGetValue(jobId, out var entry))
                continue;

            // Re-checked and removed under the entry's lock, as the sweep does. A finished run stays finished (nothing
            // re-arms one), so this only guards against an entry that is already gone.
            lock (entry)
            {
                if (entry.Record.IsTerminal && _entries.TryRemove(jobId, out _))
                    removed++;
            }
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<int> CountRunsKeptAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return Task.FromResult(OwnedBy(ownerId).Count);
    }

    /// <inheritdoc/>
    public IReadOnlyList<RunRecord> GetLiveRunsOwnedBy(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return [.. OwnedBy(ownerId).Where(r => !r.IsTerminal)];
    }

    /// <summary>A snapshot of every run <paramref name="ownerId"/> owns, in every tenant, each read under its lock.</summary>
    private List<RunRecord> OwnedBy(string ownerId)
    {
        var owned = new List<RunRecord>();
        foreach (var entry in _entries.Values)
        {
            lock (entry)
            {
                if (ScopeIdentity.AreSame(entry.Record.OwnerId, ownerId))
                    owned.Add(entry.Record);
            }
        }

        return owned;
    }
}

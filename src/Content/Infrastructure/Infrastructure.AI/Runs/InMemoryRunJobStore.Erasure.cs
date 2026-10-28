using Application.AI.Common.Interfaces.Compliance;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Runs;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Runs;

/// <summary>Right-to-erasure (#926): the person's finished runs.</summary>
public sealed partial class InMemoryRunJobStore : IPersonalDataEraser, ILiveRunSource
{
    /// <inheritdoc/>
    public string ErasureStoreName => "workflow and evaluation runs";

    /// <inheritdoc/>
    /// <remarks>Nothing else is found through a run record: a planned task's ids come from the plan store.</remarks>
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.Owner;

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
    /// on, which only the cancel path withdraws; and a queued one is a run its caller is still waiting on. Erasure stops
    /// the person's planned tasks before any store is asked (#907), so a workflow run in this process has usually ended by
    /// now. What is kept is counted by <see cref="CountLiveRunsAsync"/>, and the receipt says to run erasure again.
    /// </para>
    /// <para>
    /// Every holder of state under a removed run's id is told, as for the sweep, so nothing keyed by it is stranded.
    /// </para>
    /// </remarks>
    public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var removed = new List<string>();
        foreach (var (jobId, entry) in _entries)
        {
            // Decided and removed under the entry's lock, as the sweep does, so a run cannot be claimed or resumed between
            // the check and the removal.
            lock (entry)
            {
                if (!entry.Record.IsTerminal || !ScopeIdentity.AreSame(entry.Record.OwnerId, subject.OwnerId))
                    continue;

                if (_entries.TryRemove(jobId, out _))
                    removed.Add(jobId);
            }
        }

        RunReclaimNotifier.Notify(_reclaimListeners, removed, _logger);
        return Task.FromResult(removed.Count);
    }

    /// <inheritdoc/>
    public Task<int> CountLiveRunsAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return Task.FromResult(OwnedBy(ownerId).Count(r => !r.IsTerminal));
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

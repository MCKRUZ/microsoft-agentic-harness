using Application.AI.Common.Interfaces.Compliance;
using Domain.AI.Bundles;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Bundles;

/// <summary>Right-to-erasure (#990): the person's finished bundle runs.</summary>
public sealed partial class InMemoryBundleRunJobStore : IPersonalDataEraser
{
    /// <inheritdoc />
    public string ErasureStoreName => "bundle runs";

    /// <inheritdoc />
    /// <remarks>
    /// A run names the conversation it continued, but nothing is found through the run record: the conversation store
    /// finds the person's conversations by owner.
    /// </remarks>
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.Owner;

    /// <inheritdoc />
    public string KeptRunsStoreName => "bundle runs still held";

    /// <inheritdoc />
    public string KeptRunsReason =>
        "a bundle run record of the person's was kept: a run still queued or running, or one that ended or started while erasure ran; run erasure again once the person's bundle runs have ended";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Matches the owner through <see cref="ScopeIdentity.AreSame"/>, the comparison the run store's erasure uses, so a
    /// record that carries the id in another casing is still the person's.
    /// </para>
    /// <para>
    /// <strong>Only runs that are no longer live, by the rule admission uses</strong> (<see cref="IsLive"/>): a finished
    /// run, and a streaming reservation nobody connected to within its window. A run still queued or running is kept,
    /// as the workflow run store keeps one: removing a queued run's record makes the dispatcher drop it silently while
    /// its caller still polls for it, and removing a running one loses its outcome mid-run and frees the conversation for
    /// a second run while the first is still writing to it. What is kept is counted by <see cref="CountRunsKeptAsync"/>,
    /// and the receipt says to run erasure again.
    /// </para>
    /// <para>
    /// Nothing else is kept under a bundle run's id (the dispatch queue holds only the ids of queued runs, which are kept),
    /// so unlike the workflow run store there is no other holder to release first.
    /// </para>
    /// </remarks>
    public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var now = _time.GetUtcNow();
        var removed = 0;
        foreach (var (jobId, entry) in _entries)
        {
            bool erasable;
            lock (entry)
            {
                erasable = ScopeIdentity.AreSame(entry.Record.OwnerId, subject.OwnerId) && !IsLive(entry, now);
            }

            // Compare-and-remove, as the sweep does: an entry replaced since it was read is left for the next request.
            if (erasable && _entries.TryRemove(new KeyValuePair<string, JobEntry>(jobId, entry)))
                removed++;
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<int> CountRunsKeptAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return Task.FromResult(OwnedBy(ownerId).Count);
    }

    /// <summary>A snapshot of every run record <paramref name="ownerId"/> owns, live or not, each read under its lock.</summary>
    private List<BundleRunRecord> OwnedBy(string ownerId)
    {
        var owned = new List<BundleRunRecord>();
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

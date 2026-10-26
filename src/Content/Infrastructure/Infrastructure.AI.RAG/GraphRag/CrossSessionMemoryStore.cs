using System.Collections.Concurrent;
using System.Diagnostics;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>
/// Session-local in-memory cache for cross-session memories with background sync to the graph
/// backend. Supports Remember / Recall / Forget / Improve semantics with weight-based pruning.
/// </summary>
/// <remarks>
/// <para>
/// All read and write operations work directly against the in-process
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> cache so every call is synchronous at the
/// data layer. A background <see cref="Timer"/> flushes dirty entries to the
/// <see cref="IGraphDatabaseBackend"/> on the configured <c>SyncInterval</c>.
/// </para>
/// <para>
/// Thread safety: <see cref="_cache"/> and <see cref="_dirty"/> are both
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> instances. Individual record updates use
/// <c>AddOrUpdate</c> for atomic compare-and-replace semantics.
/// </para>
/// <para>
/// <strong>Pruning:</strong> when the cache would exceed <c>MaxMemories</c> after a
/// <see cref="RememberAsync"/> call the lowest-weight entry is evicted (ties broken by oldest
/// <c>LastAccessedAt</c>). Pruned entries are not forwarded to <see cref="ForgetAsync"/> —
/// they are simply removed from the cache. Background sync only pushes surviving dirty entries.
/// </para>
/// </remarks>
[PersonalDataStore(
    "Cross-session memory records cached in process and synced to the graph database backend, stamped with their owner. A memory not recalled or written for Rag:CrossSessionMemory:Retention:RetainFor (default two years) is deleted from both; owner erasure purges them, and their copy of their data holds them, read from both.",
    PersonalDataCategories.Identifiers | PersonalDataCategories.DerivedKnowledge,
    PersonalDataKey.Owner,
    RetentionBasis.Configured,
    ErasureSupport.PurgeByOwner,
    ExportSupport.ByOwner)]
public sealed partial class CrossSessionMemoryStore : ICrossSessionMemoryStore, IDisposable
{
    private static readonly ActivitySource _activitySource =
        new("Infrastructure.AI.RAG.GraphRag");

    /// <summary>
    /// Graph node type used when a memory record is synced to the backend. Owner-scoped purge
    /// filters on this so a right-to-erasure over the shared backend only removes memory nodes,
    /// never corpus or other subsystems' owned nodes.
    /// </summary>
    private const string MemoryNodeType = "Memory";

    private readonly IGraphDatabaseBackend _graphBackend;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<CrossSessionMemoryStore> _logger;

    private readonly ConcurrentDictionary<string, MemoryRecord> _cache = new();
    private readonly ConcurrentDictionary<string, bool> _dirty = new();

    private readonly Timer _syncTimer;

    /// <summary>
    /// Initializes a new instance of <see cref="CrossSessionMemoryStore"/>.
    /// </summary>
    /// <param name="graphBackend">Graph database backend used for durable persistence.</param>
    /// <param name="configMonitor">Options monitor providing live <see cref="AppConfig"/> values.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public CrossSessionMemoryStore(
        IGraphDatabaseBackend graphBackend,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<CrossSessionMemoryStore> logger)
    {
        _graphBackend = graphBackend;
        _configMonitor = configMonitor;
        _logger = logger;

        var interval = configMonitor.CurrentValue.AI.Rag.CrossSessionMemory.SyncInterval;
        _syncTimer = new Timer(
            callback: _ => _ = SyncToBackendAsync(),
            state: null,
            dueTime: interval,
            period: interval);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <strong>An id belongs to one owner.</strong> A write under an id another owner already holds, in the cache or the
    /// durable backend, is refused with <see cref="InvalidOperationException"/>. Overwriting it would put this owner's
    /// content in a row the backend still records as the other owner's (its update keeps the owner it first stored), so
    /// that owner's copy of their data would carry it, their erasure would delete it, and this owner's erasure would leave
    /// it behind. A write under the id of a backend node that is not a memory is refused too: the backend also holds
    /// document-derived and other subsystems' nodes, and a memory must never overwrite one. Callers that key memories by a
    /// stable name should namespace the id by owner.
    /// </para>
    /// </remarks>
    public async Task RememberAsync(MemoryRecord memory, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("CrossSessionMemory.Remember");
        activity?.SetTag("memory.id", memory.Id);

        // Canonicalize the owner on write so owner-scoped recall and right-to-erasure purge
        // match regardless of casing (mirrors ComplianceAwareGraphStore's owner stamping).
        var canonical = memory with { OwnerId = ScopeIdentity.Canonicalize(memory.OwnerId) };

        // A memory already cached passed this check when it was written, and the backend row it syncs to is its own.
        if (!_cache.ContainsKey(canonical.Id))
            await RefuseIfTheBackendHoldsTheIdForSomeoneElseAsync(canonical, cancellationToken).ConfigureAwait(false);

        // Checked inside the update, so two writers of one id under different owners cannot both get in.
        _cache.AddOrUpdate(canonical.Id, canonical, (_, existing) =>
            ScopeIdentity.AreSame(existing.OwnerId, canonical.OwnerId) ? canonical : throw HeldByAnotherOwner());
        _dirty[canonical.Id] = true;

        PruneToCapacity();
    }

    private async Task RefuseIfTheBackendHoldsTheIdForSomeoneElseAsync(MemoryRecord memory, CancellationToken cancellationToken)
    {
        var stored = await _graphBackend.GetNodeAsync(memory.Id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return;

        if (!string.Equals(stored.Type, MemoryNodeType, StringComparison.Ordinal))
            throw new InvalidOperationException("A memory cannot take the id of a graph node that is not a memory.");

        if (!ScopeIdentity.AreSame(stored.OwnerId, memory.OwnerId))
            throw HeldByAnotherOwner();
    }

    private static InvalidOperationException HeldByAnotherOwner() =>
        new("A memory with this id belongs to another owner; namespace the id by owner.");

    /// <inheritdoc/>
    public Task<IReadOnlyList<MemoryRecord>> RecallAsync(
        MemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("CrossSessionMemory.Recall");
        activity?.SetTag("memory.query", query.Query);

        var terms = query.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var matches = _cache.Values
            .Where(r => r.Weight >= query.MinWeight)
            .Where(r => query.Source is null || r.Source == query.Source)
            .Where(r => terms.Any(t =>
                r.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Weight)
            .ThenByDescending(r => r.AccessCount)
            .Take(query.TopK)
            .ToList();

        // Bump access metadata on matched records.
        var now = DateTimeOffset.UtcNow;
        var updated = new List<MemoryRecord>(matches.Count);
        foreach (var match in matches)
        {
            var bumped = match with
            {
                AccessCount = match.AccessCount + 1,
                LastAccessedAt = now
            };
            _cache.AddOrUpdate(match.Id, bumped, (_, _) => bumped);
            _dirty[match.Id] = true;
            updated.Add(bumped);
        }

        IReadOnlyList<MemoryRecord> result = updated;
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public async Task ForgetAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("CrossSessionMemory.Forget");
        activity?.SetTag("memory.id", memoryId);

        _cache.TryRemove(memoryId, out _);
        _dirty.TryRemove(memoryId, out _);

        await _graphBackend.DeleteNodeAsync(memoryId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> PurgeByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("CrossSessionMemory.PurgeByOwner");

        var canonical = ScopeIdentity.Canonicalize(ownerId);
        if (canonical is null)
            return 0;

        // 1. Purge the in-memory cache (authoritative for records not yet synced or already
        //    loaded this session).
        var cacheVictims = CachedMemoriesOwnedBy(canonical).Select(r => r.Id).ToList();
        foreach (var id in cacheVictims)
        {
            _cache.TryRemove(id, out _);
            _dirty.TryRemove(id, out _);
        }

        // 2. Purge the durable backend, memory nodes only: it also holds other subsystems' nodes.
        var backendMemoryIds = (await BackendMemoriesOwnedByAsync(canonical, cancellationToken).ConfigureAwait(false))
            .Select(n => n.Id)
            .ToList();
        if (backendMemoryIds.Count > 0)
            await _graphBackend.DeleteNodesAsync(backendMemoryIds, cancellationToken).ConfigureAwait(false);

        var purged = cacheVictims
            .Concat(backendMemoryIds)
            .Distinct(StringComparer.Ordinal)
            .Count();

        // No owner in the log or the trace: purging by owner is right-to-erasure, and the owner is the identity it removes.
        _logger.LogInformation("CrossSessionMemoryStore: purged {Count} memory records for one owner", purged);

        return purged;
    }

    /// <inheritdoc/>
    public Task ImproveAsync(
        string memoryId,
        double feedbackDelta,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("CrossSessionMemory.Improve");
        activity?.SetTag("memory.id", memoryId);
        activity?.SetTag("memory.delta", feedbackDelta);

        if (!_cache.TryGetValue(memoryId, out var existing))
        {
            _logger.LogWarning("ImproveAsync: memory {Id} not found in cache — skipping", memoryId);
            return Task.CompletedTask;
        }

        var newWeight = Math.Clamp(existing.Weight + feedbackDelta, 0.0, 1.0);
        var updated = existing with { Weight = newWeight };
        _cache.AddOrUpdate(memoryId, updated, (_, _) => updated);
        _dirty[memoryId] = true;

        return Task.CompletedTask;
    }

    // ── Internal sync ────────────────────────────────────────────────────────

    /// <summary>
    /// Flushes dirty cache entries to the graph backend and clears the dirty set.
    /// Called on the background timer interval.
    /// </summary>
    internal async Task SyncToBackendAsync()
    {
        var dirtyIds = _dirty.Keys.ToList();
        if (dirtyIds.Count == 0)
            return;

        _logger.LogDebug("CrossSessionMemoryStore: syncing {Count} dirty entries to graph backend", dirtyIds.Count);

        var nodes = new List<GraphNode>(dirtyIds.Count);
        var syncedIds = new List<string>(dirtyIds.Count);
        foreach (var id in dirtyIds)
        {
            // Clear the dirty flag BEFORE snapshotting the record. A concurrent
            // RememberAsync/ImproveAsync/RecallAsync that lands after this removal
            // re-sets the flag and is picked up by the next flush; one that landed
            // before is captured by the TryGetValue below. Clearing the flag after
            // the read (the previous behavior) silently dropped writes interleaved
            // between the read and the removal.
            _dirty.TryRemove(id, out _);

            if (!_cache.TryGetValue(id, out var record))
                continue;

            syncedIds.Add(id);
            nodes.Add(new GraphNode
            {
                Id = record.Id,
                Name = record.Id,
                Type = MemoryNodeType,
                // Persist the owner so the durable backend row can be found by an owner-scoped
                // right-to-erasure purge even after the cache entry is evicted.
                OwnerId = record.OwnerId,
                Properties = new Dictionary<string, string>
                {
                    ["content"] = record.Content,
                    ["source"] = record.Source,
                    ["weight"] = record.Weight.ToString("R"),
                    ["access_count"] = record.AccessCount.ToString(),
                    ["last_accessed_at"] = record.LastAccessedAt.ToString("O"),
                    ["created_at"] = record.CreatedAt.ToString("O")
                }
            });
        }

        try
        {
            await _graphBackend.AddNodesAsync(nodes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Re-dirty the records we attempted to flush so the next interval retries
            // them. Use the non-overwriting setter (a concurrent writer may have set
            // a fresher dirty flag in the meantime; we must not clobber that).
            foreach (var id in syncedIds)
                _dirty.TryAdd(id, true);

            _logger.LogError(ex, "CrossSessionMemoryStore: failed to sync dirty entries to graph backend");
        }
    }

    // ── Pruning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Evicts the lowest-weight memory when the cache exceeds <c>MaxMemories</c>.
    /// Ties are broken by oldest <c>LastAccessedAt</c>.
    /// </summary>
    private void PruneToCapacity()
    {
        var max = _configMonitor.CurrentValue.AI.Rag.CrossSessionMemory.MaxMemories;

        while (_cache.Count > max)
        {
            var victim = _cache.Values
                .OrderBy(r => r.Weight)
                .ThenBy(r => r.LastAccessedAt)
                .FirstOrDefault();

            if (victim is null)
                break;

            _cache.TryRemove(victim.Id, out _);
            _dirty.TryRemove(victim.Id, out _);

            _logger.LogDebug(
                "CrossSessionMemoryStore: pruned memory {Id} (weight={Weight:F4})",
                victim.Id, victim.Weight);
        }
    }

    // ── Retention ────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes every memory last recalled or written before <paramref name="cutoff"/> (#901), from the cache and from the
    /// durable backend, every owner.
    /// </summary>
    /// <remarks>
    /// The backend keeps memories across restarts while the cache starts empty, so both are swept. A backend row is kept
    /// when the cache holds the same memory with a newer access the next sync has not written yet. Only nodes of the memory
    /// type are touched; the backend also holds document-derived graph content.
    /// </remarks>
    /// <param name="cutoff">Memories with no access since this instant are deleted.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many memories were deleted.</returns>
    public async Task<int> DeleteNotAccessedSinceAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // Removed only if unchanged since read: a recall in between renews the record, and must keep it.
        var cacheVictims = new List<string>();
        foreach (var stale in _cache.Values.Where(r => r.LastAccessedAt < cutoff).ToList())
        {
            if (!_cache.TryRemove(new KeyValuePair<string, MemoryRecord>(stale.Id, stale)))
                continue;
            _dirty.TryRemove(stale.Id, out _);
            cacheVictims.Add(stale.Id);
        }

        // Memory nodes have no chunk references; a document-derived entity an extractor happened to type "Memory" does.
        var backendVictims = (await _graphBackend.GetAllNodesAsync(cancellationToken).ConfigureAwait(false))
            .Where(n => string.Equals(n.Type, MemoryNodeType, StringComparison.Ordinal) && n.ChunkIds.Count == 0)
            .Where(n => LastAccess(n) < cutoff)
            .Where(n => !_cache.TryGetValue(n.Id, out var live) || live.LastAccessedAt < cutoff)
            .Select(n => n.Id)
            .ToList();
        if (backendVictims.Count > 0)
            await _graphBackend.DeleteNodesAsync(backendVictims, cancellationToken).ConfigureAwait(false);

        return cacheVictims.Concat(backendVictims).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>A synced memory's last access, falling back to its creation; unreadable means it is never treated as old.</summary>
    private static DateTimeOffset LastAccess(GraphNode node)
    {
        foreach (var key in (string[])["last_accessed_at", "created_at"])
        {
            if (node.Properties.TryGetValue(key, out var text)
                && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                return at;
        }

        return node.CreatedAt ?? DateTimeOffset.MaxValue;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _syncTimer.Dispose();
    }
}

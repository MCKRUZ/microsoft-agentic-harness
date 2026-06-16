using System.Collections.Concurrent;
using System.Diagnostics;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
public sealed class CrossSessionMemoryStore : ICrossSessionMemoryStore, IDisposable
{
    private static readonly ActivitySource _activitySource =
        new("Infrastructure.AI.RAG.GraphRag");

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
    public Task RememberAsync(MemoryRecord memory, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("CrossSessionMemory.Remember");
        activity?.SetTag("memory.id", memory.Id);

        _cache.AddOrUpdate(memory.Id, memory, (_, _) => memory);
        _dirty[memory.Id] = true;

        PruneToCapacity();

        return Task.CompletedTask;
    }

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
                Type = "Memory",
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

    // ── IDisposable ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _syncTimer.Dispose();
    }
}

using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// <see cref="IKnowledgeMemory"/> implementation providing two-source recall
/// (session cache first, then graph), session-local caching for fast retrieval,
/// and feedback-driven improvement of knowledge quality.
/// </summary>
/// <remarks>
/// Registered as <c>Scoped</c> lifetime so each session gets its own cache.
/// The session cache is flushed to the graph store when the scope is disposed,
/// or explicitly via <see cref="ISessionKnowledgeCache.FlushToGraphAsync"/>.
/// </remarks>
public sealed class KnowledgeMemoryService : IKnowledgeMemory
{
    private readonly ISessionKnowledgeCache _sessionCache;
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IKnowledgeScope _scope;
    private readonly IFeedbackDetector? _feedbackDetector;
    private readonly IFeedbackStore? _feedbackStore;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<KnowledgeMemoryService> _logger;
    private readonly IMemoryWriteGate? _writeGate;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeMemoryService"/> class.
    /// </summary>
    /// <param name="sessionCache">Session-local knowledge cache.</param>
    /// <param name="graphStore">Permanent knowledge graph store.</param>
    /// <param name="scope">The current request's knowledge scope (user/tenant). Memory keys are
    /// namespaced by this scope so one user can never recall another user's remembered facts.</param>
    /// <param name="feedbackDetector">Feedback detector (null when feedback disabled).</param>
    /// <param name="feedbackStore">Feedback weight store (null when feedback disabled).</param>
    /// <param name="configMonitor">Application configuration.</param>
    /// <param name="logger">Logger for recording memory operations.</param>
    /// <param name="writeGate">Memory write gate that scans, classifies, and stamps provenance on
    /// facts before persistence. When <see langword="null"/> (gate not registered), writes pass
    /// through unguarded — preserving legacy behavior.</param>
    public KnowledgeMemoryService(
        ISessionKnowledgeCache sessionCache,
        IKnowledgeGraphStore graphStore,
        IKnowledgeScope scope,
        IFeedbackDetector? feedbackDetector,
        IFeedbackStore? feedbackStore,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<KnowledgeMemoryService> logger,
        IMemoryWriteGate? writeGate = null)
    {
        ArgumentNullException.ThrowIfNull(sessionCache);
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionCache = sessionCache;
        _graphStore = graphStore;
        _scope = scope;
        _feedbackDetector = feedbackDetector;
        _feedbackStore = feedbackStore;
        _configMonitor = configMonitor;
        _logger = logger;
        _writeGate = writeGate;
    }

    /// <inheritdoc />
    public async Task RememberAsync(
        string key,
        string content,
        string entityType = "Fact",
        CancellationToken cancellationToken = default)
    {
        // Gate the write before anything is persisted: scan for injection, classify trust, and
        // stamp provenance. This is the single chokepoint covering every write — including the
        // unattended post-turn auto-extraction path that bypasses the request pipeline.
        var decision = _writeGate is null
            ? null
            : await _writeGate.EvaluateAsync(key, content, entityType, cancellationToken);

        if (decision is { Persist: false })
        {
            _logger.LogWarning(
                "Memory write blocked for Key={Key}, Type={Type}: {Reason}",
                key, entityType, decision.Reason);
            return;
        }

        var node = new GraphNode
        {
            Id = MemoryNodeId(key),
            Name = key,
            Type = entityType,
            Properties = new Dictionary<string, string> { ["content"] = content },
            ChunkIds = [],
            OwnerId = _scope.UserId,
            TenantId = _scope.TenantId,
            Provenance = decision?.Provenance
        };

        // Only quarantined facts carry an explicit trust marker. Trusted facts stay unmarked —
        // GetTrust defaults unmarked nodes to Trusted — so a trusted write, a disabled-guard write,
        // and a no-gate write are all indistinguishable and recallable.
        var quarantined = decision is { Trust: MemoryTrust.Untrusted };
        if (quarantined)
            node = node.WithTrust(MemoryTrust.Untrusted);

        // Fast path for any same-scope recall within this request. Quarantined facts are kept out of
        // the cache as an optimization (no point caching what recall will filter); the authoritative
        // "never serve quarantined" enforcement lives in RecallAsync.
        if (!quarantined)
            _sessionCache.Add(node);

        // Durable write so the fact survives the request scope and is recallable in future
        // sessions. The node carries an explicit scope-namespaced Id + OwnerId, so it is
        // correctly attributed even when persisted from the post-turn background task (where
        // the ambient request scope is no longer established). Quarantined facts are still
        // persisted here (marked untrusted) so they remain available for audit and incident response.
        await _graphStore.AddNodesAsync([node], cancellationToken);

        _logger.LogDebug(
            "Remembered: Key={Key}, Type={Type}, Trust={Trust}",
            key, entityType, quarantined ? MemoryTrust.Untrusted : MemoryTrust.Trusted);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> RecallAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        // RecallAsync is the single chokepoint that enforces "quarantined facts are never served":
        // both sources are passed through IsRecallable here, so the trust invariant lives in one
        // place and cannot be bypassed by a future read path or a stray cache insertion.

        // Source 1: Session cache (fast, sub-millisecond)
        var cached = _sessionCache.Search(query, maxResults).Where(IsRecallable).ToList();
        if (cached.Count >= maxResults)
        {
            _logger.LogDebug("Recall satisfied from session cache: {Count} results", cached.Count);
            return cached;
        }

        // Source 2: Graph store (full traversal)
        var remaining = maxResults - cached.Count;
        var cachedIds = cached.Select(n => n.Id).ToHashSet();

        var graphResults = await SearchGraphAsync(query, remaining + cached.Count, cancellationToken);
        var deduped = graphResults
            .Where(n => IsRecallable(n) && !cachedIds.Contains(n.Id))
            .Take(remaining)
            .ToList();

        var combined = cached.Concat(deduped).ToList();
        _logger.LogDebug(
            "Recall: {CacheHits} from cache, {GraphHits} from graph, {Total} total",
            cached.Count, deduped.Count, combined.Count);

        return combined;
    }

    /// <inheritdoc />
    public async Task ForgetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var nodeId = MemoryNodeId(key);

        _sessionCache.Remove(nodeId);
        await _graphStore.DeleteNodeAsync(nodeId, cancellationToken);

        _logger.LogDebug("Forgot: Key={Key}", key);
    }

    /// <inheritdoc />
    public async Task ImproveAsync(
        string userMessage,
        string assistantResponse,
        IReadOnlyList<string> relevantNodeIds,
        CancellationToken cancellationToken = default)
    {
        if (_feedbackDetector is null || _feedbackStore is null)
        {
            _logger.LogDebug("Feedback not enabled; skipping Improve");
            return;
        }

        var detection = await _feedbackDetector.DetectFeedbackAsync(
            userMessage, assistantResponse, cancellationToken);

        if (!detection.FeedbackDetected || detection.FeedbackScore is null)
            return;

        var alpha = _configMonitor.CurrentValue.AI.Rag.GraphRag.FeedbackAlpha;
        foreach (var nodeId in relevantNodeIds)
        {
            await _feedbackStore.ApplyNodeFeedbackAsync(
                nodeId, detection.FeedbackScore.Value, alpha, cancellationToken);
        }

        _logger.LogInformation(
            "Improved {NodeCount} nodes with feedback score {Score}",
            relevantNodeIds.Count, detection.FeedbackScore);
    }

    private async Task<IReadOnlyList<GraphNode>> SearchGraphAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matched = new List<GraphNode>();
        var seen = new HashSet<string>();

        // Try direct memory key lookups first. Keys are namespaced by the current scope so a
        // user only ever queries their own remembered facts — never another user's or tenant's.
        foreach (var term in terms)
        {
            if (matched.Count >= maxResults) break;
            var memoryId = MemoryNodeId(term);
            var node = await _graphStore.GetNodeAsync(memoryId, cancellationToken);
            if (node is not null && seen.Add(node.Id))
                matched.Add(node);
        }

        if (matched.Count >= maxResults)
            return matched;

        // Search via triplets from any matched nodes' neighborhoods
        if (matched.Count > 0)
        {
            var neighborIds = matched.Select(n => n.Id).ToList();
            var triplets = await _graphStore.GetTripletsAsync(neighborIds, cancellationToken);
            foreach (var t in triplets)
            {
                if (matched.Count >= maxResults) break;
                if (MatchesQuery(t.Source, terms) && seen.Add(t.Source.Id))
                    matched.Add(t.Source);
                if (matched.Count >= maxResults) break;
                if (MatchesQuery(t.Target, terms) && seen.Add(t.Target.Id))
                    matched.Add(t.Target);
            }
        }

        // Scan neighbors of nodes found by direct ID lookup (entity-style IDs)
        foreach (var term in terms)
        {
            if (matched.Count >= maxResults) break;
            var entityId = $"{term.ToLowerInvariant()}:entity";
            var node = await _graphStore.GetNodeAsync(entityId, cancellationToken);
            if (node is not null && seen.Add(node.Id))
                matched.Add(node);

            if (node is null) continue;
            var neighbors = await _graphStore.GetNeighborsAsync(entityId, maxDepth: 1, cancellationToken);
            foreach (var neighbor in neighbors)
            {
                if (matched.Count >= maxResults) break;
                if (MatchesQuery(neighbor, terms) && seen.Add(neighbor.Id))
                    matched.Add(neighbor);
            }
        }

        return matched;
    }

    private static bool MatchesQuery(GraphNode node, string[] terms)
    {
        return terms.Any(t =>
            node.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            node.Type.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    // Quarantined (untrusted-provenance) facts are persisted for audit but never served by recall.
    // Implements the "treat retrieval as a risk decision" principle: trust is re-checked at read.
    private static bool IsRecallable(GraphNode node) => node.GetTrust() == MemoryTrust.Trusted;

    /// <summary>
    /// Builds the deterministic, scope-namespaced node id for a remembered fact. Two users (or
    /// two tenants) that remember the same key get distinct ids, so recall — which looks facts up
    /// by id — can never cross the scope boundary. When scope is unset (single-tenant deployment
    /// that has not wired <c>SetScope</c>), all callers share the configured default namespace,
    /// preserving the prior single-tenant behavior.
    /// </summary>
    private string MemoryNodeId(string key)
        => $"memory:{ScopeKey()}:{key.Trim().ToLowerInvariant()}";

    private string ScopeKey()
    {
        var tenant = Sanitize(_scope.TenantId) ?? "default";
        var user = Sanitize(_scope.UserId) ?? "anon";
        return $"{tenant}:{user}";
    }

    private static string? Sanitize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant().Replace(':', '_');
}

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
    private readonly IFeedbackDetector? _feedbackDetector;
    private readonly IFeedbackStore? _feedbackStore;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<KnowledgeMemoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeMemoryService"/> class.
    /// </summary>
    /// <param name="sessionCache">Session-local knowledge cache.</param>
    /// <param name="graphStore">Permanent knowledge graph store.</param>
    /// <param name="feedbackDetector">Feedback detector (null when feedback disabled).</param>
    /// <param name="feedbackStore">Feedback weight store (null when feedback disabled).</param>
    /// <param name="configMonitor">Application configuration.</param>
    /// <param name="logger">Logger for recording memory operations.</param>
    public KnowledgeMemoryService(
        ISessionKnowledgeCache sessionCache,
        IKnowledgeGraphStore graphStore,
        IFeedbackDetector? feedbackDetector,
        IFeedbackStore? feedbackStore,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<KnowledgeMemoryService> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionCache);
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionCache = sessionCache;
        _graphStore = graphStore;
        _feedbackDetector = feedbackDetector;
        _feedbackStore = feedbackStore;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task RememberAsync(
        string key,
        string content,
        string entityType = "Fact",
        CancellationToken cancellationToken = default)
    {
        var nodeId = $"memory:{key.ToLowerInvariant()}";
        var node = new GraphNode
        {
            Id = nodeId,
            Name = key,
            Type = entityType,
            Properties = new Dictionary<string, string> { ["content"] = content },
            ChunkIds = []
        };

        _sessionCache.Add(node);
        _logger.LogDebug("Remembered: Key={Key}, Type={Type}", key, entityType);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> RecallAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        // Source 1: Session cache (fast, sub-millisecond)
        var cached = _sessionCache.Search(query, maxResults);
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
            .Where(n => !cachedIds.Contains(n.Id))
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
        var nodeId = $"memory:{key.ToLowerInvariant()}";

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

        // Try direct memory key lookups first
        foreach (var term in terms)
        {
            if (matched.Count >= maxResults) break;
            var memoryId = $"memory:{term.ToLowerInvariant()}";
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
}

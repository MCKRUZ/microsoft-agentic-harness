using System.Globalization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// Graph-backed <see cref="IEpisodicSegmentStore"/> using deterministic node IDs and a per-conversation
/// index node for grouped retrieval. Mirrors <c>GraphWorkEpisodeStore</c> — the two share the turn-boundary
/// capture seam but stay distinct records, cross-linked by <see cref="EpisodicSegment.EpisodeId"/> and the
/// <c>(ConversationId, TurnNumber)</c> pair.
/// </summary>
/// <remarks>
/// <para>
/// Tenant isolation is inherited from the injected <see cref="IKnowledgeGraphStore"/>, which in
/// production is the tenant-isolating / compliance-aware decorator chain (stamps tenant on write,
/// filters on read), exactly as <c>GraphWorkEpisodeStore</c> does.
/// </para>
/// <para>
/// Owner attribution is stamped <strong>here</strong> for the same reason as
/// <c>GraphWorkEpisodeStore</c>: the compliance decorator leaves <see cref="GraphNode.OwnerId"/>
/// writer-authoritative, so an unstamped segment node would persist owner-less and escape
/// owner-scoped right-to-erasure. Each saved node is stamped with the caller's canonical owner,
/// resolved per-operation from the ambient request scope so this singleton store never captures the
/// scoped <see cref="IKnowledgeScope"/>.
/// </para>
/// </remarks>
public sealed class GraphEpisodicSegmentStore : IEpisodicSegmentStore
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly ILogger<GraphEpisodicSegmentStore> _logger;

    private const string NodePrefix = "episodicsegment:";
    private const string IndexPrefix = "episodicsegmentindex:conv:";
    private const string NodeType = "EpisodicSegment";
    private const string IndexType = "EpisodicSegmentIndex";
    private const string EdgePredicate = "has_segment";
    private const string ChunkId = "episodicsegmentindex";

    /// <summary>Initializes a new instance of the <see cref="GraphEpisodicSegmentStore"/> class.</summary>
    /// <param name="graphStore">The knowledge graph store nodes are persisted to.</param>
    /// <param name="ambientScope">The ambient request scope used to resolve the caller's owner
    /// per-operation for owner stamping (see the class remarks).</param>
    /// <param name="logger">Logger for recording episodic-segment persistence operations.</param>
    public GraphEpisodicSegmentStore(
        IKnowledgeGraphStore graphStore,
        IAmbientRequestScope ambientScope,
        ILogger<GraphEpisodicSegmentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(logger);

        _graphStore = graphStore;
        _ambientScope = ambientScope;
        _logger = logger;
    }

    /// <summary>
    /// The canonical owner ID of the caller in flight, resolved per-operation from the ambient
    /// request scope. <see langword="null"/> for background/system work outside any request scope.
    /// </summary>
    private string? CurrentOwnerId =>
        ScopeIdentity.Canonicalize(_ambientScope.Current?.GetService<IKnowledgeScope>()?.UserId);

    /// <inheritdoc />
    public async Task<Result> SaveAsync(EpisodicSegment segment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(segment);
        try
        {
            var nodeId = ToNodeId(segment.SegmentId);
            var node = new GraphNode
            {
                Id = nodeId,
                Name = $"Segment: {segment.ConversationId}#{segment.TurnNumber}",
                Type = NodeType,
                Properties = SerializeProperties(segment),
                OwnerId = CurrentOwnerId
            };

            await _graphStore.AddNodesAsync([node], ct);
            await CreateIndexEdgeAsync(nodeId, segment.ConversationId, ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save episodic segment {SegmentId}", segment.SegmentId);
            return Result.Fail($"Failed to save episodic segment: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EpisodicSegment>>> GetByConversationAsync(string conversationId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        try
        {
            var candidates = new Dictionary<string, GraphNode>();
            await CollectIndexNeighborsAsync(ToIndexId(conversationId), candidates, ct);

            var segments = new List<EpisodicSegment>();
            foreach (var node in candidates.Values)
            {
                var id = ExtractSegmentId(node.Id);
                if (id is null) continue;

                var segment = Deserialize(id.Value, node);
                if (segment is not null)
                    segments.Add(segment);
            }

            IReadOnlyList<EpisodicSegment> ordered = segments
                .OrderByDescending(s => s.CreatedAt)
                .ToList();
            return Result<IReadOnlyList<EpisodicSegment>>.Success(ordered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get episodic segments for conversation {ConversationId}", conversationId);
            return Result<IReadOnlyList<EpisodicSegment>>.Fail($"Failed to get episodic segments: {ex.Message}");
        }
    }

    private static string ToNodeId(Guid segmentId) => $"{NodePrefix}{segmentId}".ToLowerInvariant();

    private static string ToIndexId(string conversationId) =>
        $"{IndexPrefix}{conversationId}".ToLowerInvariant();

    private static Guid? ExtractSegmentId(string nodeId)
    {
        if (!nodeId.StartsWith(NodePrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Guid.TryParse(nodeId[NodePrefix.Length..], out var id) ? id : null;
    }

    private async Task CreateIndexEdgeAsync(string nodeId, string conversationId, CancellationToken ct)
    {
        var indexId = ToIndexId(conversationId);
        var indexNode = new GraphNode { Id = indexId, Name = $"Conversation:{conversationId}", Type = IndexType };

        await _graphStore.AddNodesAsync([indexNode], ct);
        await _graphStore.AddEdgesAsync(
            [new GraphEdge
            {
                Id = $"edge:{indexId}:{nodeId}",
                SourceNodeId = indexId,
                TargetNodeId = nodeId,
                Predicate = EdgePredicate,
                ChunkId = ChunkId
            }],
            ct);
    }

    private async Task CollectIndexNeighborsAsync(string indexNodeId, Dictionary<string, GraphNode> candidates, CancellationToken ct)
    {
        if (!await _graphStore.NodeExistsAsync(indexNodeId, ct))
            return;

        var neighbors = await _graphStore.GetNeighborsAsync(indexNodeId, maxDepth: 1, ct);
        foreach (var neighbor in neighbors.Where(n => n.Type == NodeType))
            candidates.TryAdd(neighbor.Id, neighbor);
    }

    private static Dictionary<string, string> SerializeProperties(EpisodicSegment segment)
    {
        var props = new Dictionary<string, string>
        {
            ["AgentId"] = segment.AgentId,
            ["ConversationId"] = segment.ConversationId,
            ["TurnNumber"] = segment.TurnNumber.ToString(CultureInfo.InvariantCulture),
            ["Content"] = segment.Content,
            ["CreatedAt"] = segment.CreatedAt.ToString("O")
        };

        // The episode cross-link is optional (null when work memory was disabled for this turn).
        if (segment.EpisodeId is { } episodeId)
            props["EpisodeId"] = episodeId.ToString();

        return props;
    }

    private EpisodicSegment? Deserialize(Guid segmentId, GraphNode node)
    {
        var props = node.Properties;

        if (!props.ContainsKey("ConversationId"))
        {
            _logger.LogWarning("Skipping graph node {NodeId}: missing required episodic-segment properties", node.Id);
            return null;
        }

        return new EpisodicSegment
        {
            SegmentId = segmentId,
            EpisodeId = Guid.TryParse(props.GetValueOrDefault("EpisodeId", ""), out var eid) ? eid : null,
            AgentId = props.GetValueOrDefault("AgentId", ""),
            ConversationId = props.GetValueOrDefault("ConversationId", ""),
            TurnNumber = int.TryParse(props.GetValueOrDefault("TurnNumber", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tn) ? tn : 0,
            Content = props.GetValueOrDefault("Content", ""),
            CreatedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("CreatedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ca) ? ca : DateTimeOffset.UtcNow
        };
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>
/// High-level <see cref="IGraphRagService"/> implementation that delegates graph storage
/// to <see cref="IGraphDatabaseBackend"/> and provides LLM-based entity extraction,
/// community-aware global search, and graph-traversal local search.
/// </summary>
/// <remarks>
/// <para>
/// This service orchestrates the GraphRAG pipeline: entity extraction via LLM,
/// graph construction via <see cref="IGraphDatabaseBackend"/>, and search via graph
/// traversal + LLM synthesis. The graph storage backend is selected via keyed DI
/// (<c>"in_memory"</c>, <c>"postgresql"</c>, <c>"neo4j"</c>, <c>"kuzu"</c>).
/// </para>
/// <para>
/// <strong>Global search</strong> checks for pre-computed communities first. When communities
/// exist at the configured level, each community's LLM-generated summary is used for synthesis.
/// When no communities exist, the service falls back to a full triplet scan.
/// </para>
/// <para>
/// <strong>Local search</strong> matches query terms against all nodes, then uses
/// <see cref="IGraphDatabaseBackend.TraverseAsync"/> to expand each matched node's
/// neighborhood (depth 1) and collect chunk IDs for result construction.
/// </para>
/// </remarks>
public sealed class ManagedCodeGraphRagService : IGraphRagService
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.GraphRag");

    private readonly IGraphDatabaseBackend _graphBackend;
    private readonly IRagModelRouter _modelRouter;
    private readonly IProvenanceStamper _provenanceStamper;
    private readonly ICommunityDetector _communityDetector;
    private readonly ILogger<ManagedCodeGraphRagService> _logger;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagedCodeGraphRagService"/> class.
    /// </summary>
    /// <param name="graphBackend">The extended knowledge graph storage backend supporting communities and traversal.</param>
    /// <param name="modelRouter">Routes LLM calls to the appropriate model tier.</param>
    /// <param name="provenanceStamper">Stamps provenance metadata on extracted entities.</param>
    /// <param name="communityDetector">Detects communities in the graph using a partitioning algorithm.</param>
    /// <param name="logger">Logger for recording graph operations.</param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    public ManagedCodeGraphRagService(
        IGraphDatabaseBackend graphBackend,
        IRagModelRouter modelRouter,
        IProvenanceStamper provenanceStamper,
        ICommunityDetector communityDetector,
        ILogger<ManagedCodeGraphRagService> logger,
        IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(graphBackend);
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(provenanceStamper);
        ArgumentNullException.ThrowIfNull(communityDetector);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configMonitor);

        _graphBackend = graphBackend;
        _modelRouter = modelRouter;
        _provenanceStamper = provenanceStamper;
        _communityDetector = communityDetector;
        _logger = logger;
        _configMonitor = configMonitor;
    }

    /// <inheritdoc />
    public async Task IndexCorpusAsync(
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.graph.index_corpus");
        activity?.SetTag(RagConventions.IngestChunksProduced, chunks.Count);

        _logger.LogInformation("GraphRAG indexing started: {ChunkCount} chunks", chunks.Count);
        var client = _modelRouter.GetClientForOperation("graph_entity_extraction");

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extracted = await ExtractEntitiesAsync(client, chunk, cancellationToken);

            var stamp = _provenanceStamper.CreateStamp(
                "rag_ingestion", "entity_extraction",
                sourceDocumentId: chunk.DocumentId);

            var stampedNodes = extracted.Nodes
                .Select(n => _provenanceStamper.StampNode(n, stamp))
                .ToList();
            var stampedEdges = extracted.Edges
                .Select(e => _provenanceStamper.StampEdge(e, stamp))
                .ToList();

            if (stampedNodes.Count > 0)
                await _graphBackend.AddNodesAsync(stampedNodes, cancellationToken);
            if (stampedEdges.Count > 0)
                await _graphBackend.AddEdgesAsync(stampedEdges, cancellationToken);
        }

        var nodeCount = await _graphBackend.GetNodeCountAsync(cancellationToken);
        var edgeCount = await _graphBackend.GetEdgeCountAsync(cancellationToken);
        _logger.LogInformation(
            "GraphRAG indexing completed: {EntityCount} entities, {RelCount} relationships",
            nodeCount, edgeCount);
    }

    /// <inheritdoc />
    public async Task<RagAssembledContext> GlobalSearchAsync(
        string query,
        int communityLevel,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.graph.global_search");
        activity?.SetTag(RagConventions.GraphCommunityLevel, communityLevel);
        activity?.SetTag(RagConventions.RetrievalStrategy, RagConventions.StrategyValues.GraphRag);

        // Prefer community summaries — fall back to full triplet scan when none exist.
        var communities = await _graphBackend.GetCommunitiesAsync(communityLevel, cancellationToken);
        string summary;

        if (communities.Count > 0)
        {
            _logger.LogInformation(
                "GraphRAG global search: using {CommunityCount} communities at level {Level}",
                communities.Count, communityLevel);
            summary = BuildCommunitySummaryFromCommunities(communities);
        }
        else
        {
            var triplets = await _graphBackend.GetTripletsAsync([], cancellationToken);
            if (triplets.Count == 0)
            {
                return new RagAssembledContext
                {
                    AssembledText = "No entities have been indexed. Please ingest documents first.",
                    TotalTokens = 0,
                    WasTruncated = false
                };
            }

            _logger.LogInformation(
                "GraphRAG global search: no communities at level {Level}, falling back to {TripletCount} triplets",
                communityLevel, triplets.Count);
            summary = BuildCommunitySummaryFromTriplets(triplets);
        }

        var client = _modelRouter.GetClientForOperation("graph_global_search");

        var prompt = $$"""
            You are a knowledge graph analyst. Based on the following entity and relationship
            summary extracted from a document corpus, answer the user's query by synthesizing
            themes and patterns across the entire graph.

            ## Knowledge Graph Summary
            {{summary}}

            ## User Query
            {{query}}

            Provide a comprehensive answer that references specific entities and relationships.
            """;

        var response = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        var text = response.Text ?? string.Empty;

        return new RagAssembledContext
        {
            AssembledText = text,
            TotalTokens = text.Length / 4,
            WasTruncated = false
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> LocalSearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.graph.local_search");
        activity?.SetTag(RagConventions.RetrievalStrategy, RagConventions.StrategyValues.GraphRag);

        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Full scan for node matching — replace with SearchNodesAsync for production scale.
        var allNodes = await _graphBackend.GetAllNodesAsync(cancellationToken);
        if (allNodes.Count == 0)
            return [];

        var matchedNodeIds = new HashSet<string>();
        foreach (var node in allNodes)
        {
            if (queryTerms.Any(t =>
                node.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                node.Type.Contains(t, StringComparison.OrdinalIgnoreCase)))
                matchedNodeIds.Add(node.Id);
        }

        if (matchedNodeIds.Count == 0)
            return [];

        // Collect chunk IDs from matched nodes and their depth-1 neighbors via graph traversal.
        var allChunkIds = new HashSet<string>();
        foreach (var nodeId in matchedNodeIds)
        {
            var matchedNode = allNodes.FirstOrDefault(n => n.Id == nodeId);
            if (matchedNode is not null)
                foreach (var cid in matchedNode.ChunkIds) allChunkIds.Add(cid);

            var neighbors = await _graphBackend.TraverseAsync(nodeId, maxDepth: 1, cancellationToken);
            foreach (var neighbor in neighbors)
                foreach (var cid in neighbor.ChunkIds) allChunkIds.Add(cid);
        }

        activity?.SetTag("rag.graph.traversal_depth", 1);
        activity?.SetTag(RagConventions.RetrievalChunksReturned, allChunkIds.Count);

        _logger.LogInformation(
            "GraphRAG local search: {MatchedEntities} entities matched, {ChunkCount} chunks found",
            matchedNodeIds.Count, allChunkIds.Count);

        return allChunkIds
            .Take(topK)
            .Select((id, index) => new RetrievalResult
            {
                Chunk = new DocumentChunk
                {
                    Id = id,
                    DocumentId = "",
                    SectionPath = "",
                    Content = $"[Graph result from entity match — chunk {id}]",
                    Tokens = 0,
                    Metadata = new ChunkMetadata
                    {
                        SourceUri = new Uri("graph://entity-match"),
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                },
                DenseScore = 1.0 - (index * 0.05),
                SparseScore = 0.0,
                FusedScore = 1.0 - (index * 0.05)
            })
            .ToList();
    }

    private async Task<ExtractionResult> ExtractEntitiesAsync(
        IChatClient client,
        DocumentChunk chunk,
        CancellationToken cancellationToken)
    {
        var contentSnippet = chunk.Content[..Math.Min(chunk.Content.Length, 2000)];
        var prompt = $$"""
            Extract named entities and relationships from the following text.
            Return a JSON object with:
            - "entities": array of {"name": string, "type": string}
            - "relationships": array of {"source": string, "predicate": string, "target": string}

            Text:
            {{contentSnippet}}

            JSON:
            """;

        try
        {
            var response = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var json = response.Text ?? "{}";

            var startIndex = json.IndexOf('{');
            var endIndex = json.LastIndexOf('}');
            if (startIndex >= 0 && endIndex > startIndex)
                json = json[startIndex..(endIndex + 1)];

            var parsed = JsonSerializer.Deserialize<ExtractionJson>(json, JsonOptions);

            var nodes = (parsed?.Entities ?? [])
                .Select(e => new GraphNode
                {
                    Id = $"{(e.Name ?? "unknown").ToLowerInvariant()}:{(e.Type ?? "unknown").ToLowerInvariant()}",
                    Name = e.Name ?? "unknown",
                    Type = e.Type ?? "unknown",
                    ChunkIds = [chunk.Id]
                })
                .ToList();

            var edges = (parsed?.Relationships ?? [])
                .Select(r =>
                {
                    var source = $"{(r.Source ?? "unknown").ToLowerInvariant()}:entity";
                    var target = $"{(r.Target ?? "unknown").ToLowerInvariant()}:entity";
                    var predicate = r.Predicate ?? "related_to";
                    return new GraphEdge
                    {
                        Id = $"{source}|{predicate}|{target}",
                        SourceNodeId = source,
                        TargetNodeId = target,
                        Predicate = predicate,
                        ChunkId = chunk.Id
                    };
                })
                .ToList();

            return new ExtractionResult(nodes, edges);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity extraction failed for chunk {ChunkId}", chunk.Id);
            return new ExtractionResult([], []);
        }
    }

    /// <summary>
    /// Builds a community summary string from pre-computed <see cref="Community"/> records.
    /// Uses the top 20 communities to stay within reasonable token budgets.
    /// </summary>
    private static string BuildCommunitySummaryFromCommunities(IReadOnlyList<Community> communities)
    {
        var sb = new StringBuilder();
        foreach (var community in communities.Take(20))
        {
            sb.AppendLine($"### Community: {community.Id} ({community.NodeIds.Count} entities)");
            sb.AppendLine(community.Summary);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a community summary from raw triplets when no pre-computed communities exist.
    /// Used as fallback in <see cref="GlobalSearchAsync"/> when community detection has not run.
    /// </summary>
    private static string BuildCommunitySummaryFromTriplets(IReadOnlyList<GraphTriplet> triplets)
    {
        var sb = new StringBuilder();

        var nodeNames = new HashSet<string>();
        foreach (var t in triplets.Take(100))
        {
            if (nodeNames.Add(t.Source.Name))
                sb.AppendLine($"  - {t.Source.Name} ({t.Source.Type}): referenced in {t.Source.ChunkIds.Count} chunks");
            if (nodeNames.Add(t.Target.Name))
                sb.AppendLine($"  - {t.Target.Name} ({t.Target.Type}): referenced in {t.Target.ChunkIds.Count} chunks");
        }

        sb.AppendLine();
        sb.AppendLine($"Relationships ({triplets.Count}):");
        foreach (var t in triplets.Take(100))
            sb.AppendLine($"  - {t.Source.Name} --[{t.Edge.Predicate}]--> {t.Target.Name}");

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ExtractionResult(
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<GraphEdge> Edges);

    private sealed record ExtractionJson
    {
        public List<EntityJson>? Entities { get; init; }
        public List<RelationshipJson>? Relationships { get; init; }
    }

    private sealed record EntityJson
    {
        public string? Name { get; init; }
        public string? Type { get; init; }
    }

    private sealed record RelationshipJson
    {
        public string? Source { get; init; }
        public string? Predicate { get; init; }
        public string? Target { get; init; }
    }
}

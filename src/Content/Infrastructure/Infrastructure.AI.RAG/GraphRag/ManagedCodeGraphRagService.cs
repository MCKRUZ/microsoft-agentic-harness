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
/// to <see cref="IKnowledgeGraphStore"/> and provides LLM-based entity extraction,
/// global search (community-level summarization), and local search (entity-neighborhood
/// retrieval).
/// </summary>
/// <remarks>
/// <para>
/// This service orchestrates the GraphRAG pipeline: entity extraction via LLM,
/// graph construction via <see cref="IKnowledgeGraphStore"/>, and search via graph
/// traversal + LLM synthesis. The graph storage backend is selected via keyed DI
/// (<c>"in_memory"</c>, <c>"postgresql"</c>, <c>"neo4j"</c>).
/// </para>
/// <para>
/// <strong>Limitations:</strong> Community detection (Leiden algorithm) is not yet
/// implemented. The <c>communityLevel</c> parameter in <see cref="GlobalSearchAsync"/>
/// is accepted but all entities are treated as a single community.
/// </para>
/// </remarks>
public sealed class ManagedCodeGraphRagService : IGraphRagService
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.GraphRag");

    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IRagModelRouter _modelRouter;
    private readonly IProvenanceStamper _provenanceStamper;
    private readonly ILogger<ManagedCodeGraphRagService> _logger;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagedCodeGraphRagService"/> class.
    /// </summary>
    /// <param name="graphStore">The knowledge graph storage backend.</param>
    /// <param name="modelRouter">Routes LLM calls to the appropriate model tier.</param>
    /// <param name="provenanceStamper">Stamps provenance metadata on extracted entities.</param>
    /// <param name="logger">Logger for recording graph operations.</param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    public ManagedCodeGraphRagService(
        IKnowledgeGraphStore graphStore,
        IRagModelRouter modelRouter,
        IProvenanceStamper provenanceStamper,
        ILogger<ManagedCodeGraphRagService> logger,
        IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(provenanceStamper);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configMonitor);

        _graphStore = graphStore;
        _modelRouter = modelRouter;
        _provenanceStamper = provenanceStamper;
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
                await _graphStore.AddNodesAsync(stampedNodes, cancellationToken);
            if (stampedEdges.Count > 0)
                await _graphStore.AddEdgesAsync(stampedEdges, cancellationToken);
        }

        var nodeCount = await _graphStore.GetNodeCountAsync(cancellationToken);
        var edgeCount = await _graphStore.GetEdgeCountAsync(cancellationToken);
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

        var triplets = await _graphStore.GetTripletsAsync([], cancellationToken);
        if (triplets.Count == 0)
        {
            return new RagAssembledContext
            {
                AssembledText = "No entities have been indexed. Please ingest documents first.",
                TotalTokens = 0,
                WasTruncated = false
            };
        }

        var client = _modelRouter.GetClientForOperation("graph_global_search");
        var summary = BuildCommunitySummary(triplets);

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

        _logger.LogInformation(
            "GraphRAG global search completed: {TripletCount} triplets considered, Level={Level}",
            triplets.Count, communityLevel);

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

        // Full scan + client-side filter. Replace with IKnowledgeGraphStore.SearchNodesAsync for production scale.
        var triplets = await _graphStore.GetTripletsAsync([], cancellationToken);
        if (triplets.Count == 0)
            return [];

        var matchedNodeIds = new HashSet<string>();
        foreach (var triplet in triplets)
        {
            if (queryTerms.Any(t =>
                triplet.Source.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                triplet.Source.Type.Contains(t, StringComparison.OrdinalIgnoreCase)))
                matchedNodeIds.Add(triplet.Source.Id);

            if (queryTerms.Any(t =>
                triplet.Target.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                triplet.Target.Type.Contains(t, StringComparison.OrdinalIgnoreCase)))
                matchedNodeIds.Add(triplet.Target.Id);
        }

        if (matchedNodeIds.Count == 0)
            return [];

        // Get neighbor chunk IDs from matched nodes and their triplets
        var neighborTriplets = await _graphStore.GetTripletsAsync(
            matchedNodeIds.ToList(), cancellationToken);

        var allChunkIds = new HashSet<string>();
        foreach (var t in neighborTriplets)
        {
            foreach (var cid in t.Source.ChunkIds) allChunkIds.Add(cid);
            foreach (var cid in t.Target.ChunkIds) allChunkIds.Add(cid);
        }

        var nodeTasks = matchedNodeIds.Select(id => _graphStore.GetNodeAsync(id, cancellationToken));
        var nodes = await Task.WhenAll(nodeTasks);
        foreach (var node in nodes)
        {
            if (node is not null)
                foreach (var cid in node.ChunkIds) allChunkIds.Add(cid);
        }

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

    private static string BuildCommunitySummary(IReadOnlyList<GraphTriplet> triplets)
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

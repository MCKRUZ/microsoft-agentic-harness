using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>
/// Teaching implementation of <see cref="IGraphRagService"/> that demonstrates the
/// GraphRAG API shape with in-memory entity/relationship storage and LLM-based
/// entity extraction. Production implementations should use the ManagedCode.GraphRag
/// NuGet package with PostgreSQL-backed graph storage and the Leiden community
/// detection algorithm.
/// </summary>
/// <remarks>
/// <para>
/// This skeleton extracts entities and relationships from document chunks via LLM,
/// stores them in a <see cref="ConcurrentDictionary{TKey,TValue}"/>, and provides
/// global search (community-level summarization) and local search (entity-neighborhood
/// retrieval). Community detection is simplified to a single flat level.
/// </para>
/// <para>
/// <strong>Limitations vs. production GraphRAG:</strong>
/// <list type="bullet">
///   <item>No Leiden community detection — all entities are in a single community.</item>
///   <item>No multi-level hierarchy — <c>communityLevel</c> is accepted but ignored.</item>
///   <item>In-memory storage — data is lost on restart.</item>
///   <item>No incremental indexing — <see cref="IndexCorpusAsync"/> is additive but not idempotent.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ManagedCodeGraphRagService : IGraphRagService
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.GraphRag");

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<ManagedCodeGraphRagService> _logger;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    private readonly ConcurrentDictionary<string, GraphEntity> _entities = new();
    private readonly ConcurrentDictionary<string, GraphRelationship> _relationships = new();
    private readonly ConcurrentDictionary<string, DocumentChunk> _chunkIndex = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagedCodeGraphRagService"/> class.
    /// </summary>
    /// <param name="modelRouter">Routes LLM calls to the appropriate model tier.</param>
    /// <param name="logger">Logger for recording graph operations.</param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    public ManagedCodeGraphRagService(
        IRagModelRouter modelRouter,
        ILogger<ManagedCodeGraphRagService> logger,
        IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configMonitor);

        _modelRouter = modelRouter;
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
            _chunkIndex.TryAdd(chunk.Id, chunk);

            var extracted = await ExtractEntitiesAsync(client, chunk, cancellationToken);

            foreach (var entity in extracted.Entities)
            {
                _entities.AddOrUpdate(
                    entity.Name,
                    entity,
                    (_, existing) => existing with
                    {
                        ChunkIds = existing.ChunkIds.Concat(entity.ChunkIds).Distinct().ToList()
                    });
            }

            foreach (var rel in extracted.Relationships)
            {
                var key = $"{rel.Source}|{rel.Predicate}|{rel.Target}";
                _relationships.TryAdd(key, rel);
            }
        }

        _logger.LogInformation(
            "GraphRAG indexing completed: {EntityCount} entities, {RelCount} relationships",
            _entities.Count, _relationships.Count);
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

        if (_entities.IsEmpty)
        {
            return new RagAssembledContext
            {
                AssembledText = "No entities have been indexed. Please ingest documents first.",
                TotalTokens = 0,
                WasTruncated = false
            };
        }

        var client = _modelRouter.GetClientForOperation("graph_global_search");
        var summary = BuildCommunitySummary();

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
            "GraphRAG global search completed: {EntityCount} entities considered, Level={Level}",
            _entities.Count, communityLevel);

        return new RagAssembledContext
        {
            AssembledText = text,
            TotalTokens = text.Length / 4,
            WasTruncated = false
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievalResult>> LocalSearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.graph.local_search");
        activity?.SetTag(RagConventions.RetrievalStrategy, RagConventions.StrategyValues.GraphRag);

        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matchedEntities = _entities.Values
            .Where(e => queryTerms.Any(t =>
                e.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                e.Type.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var relatedChunkIds = matchedEntities
            .SelectMany(e => e.ChunkIds)
            .Concat(GetNeighborChunkIds(matchedEntities))
            .Distinct()
            .ToList();

        var results = relatedChunkIds
            .Where(id => _chunkIndex.ContainsKey(id))
            .Select((id, index) => new RetrievalResult
            {
                Chunk = _chunkIndex[id],
                DenseScore = 1.0 - (index * 0.05),
                SparseScore = 0.0,
                FusedScore = 1.0 - (index * 0.05)
            })
            .Take(topK)
            .ToList();

        _logger.LogInformation(
            "GraphRAG local search: {MatchedEntities} entities matched, {ResultCount} chunks returned",
            matchedEntities.Count, results.Count);

        return Task.FromResult<IReadOnlyList<RetrievalResult>>(results);
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

            var entities = (parsed?.Entities ?? [])
                .Select(e => new GraphEntity
                {
                    Name = e.Name ?? "unknown",
                    Type = e.Type ?? "unknown",
                    ChunkIds = [chunk.Id]
                })
                .ToList();

            var relationships = (parsed?.Relationships ?? [])
                .Select(r => new GraphRelationship
                {
                    Source = r.Source ?? "unknown",
                    Predicate = r.Predicate ?? "related_to",
                    Target = r.Target ?? "unknown",
                    ChunkId = chunk.Id
                })
                .ToList();

            return new ExtractionResult(entities, relationships);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity extraction failed for chunk {ChunkId}", chunk.Id);
            return new ExtractionResult([], []);
        }
    }

    private IEnumerable<string> GetNeighborChunkIds(IReadOnlyList<GraphEntity> entities)
    {
        var entityNames = entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _relationships.Values
            .Where(r => entityNames.Contains(r.Source) || entityNames.Contains(r.Target))
            .Select(r => r.ChunkId);
    }

    private string BuildCommunitySummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Entities ({_entities.Count}):");

        foreach (var entity in _entities.Values.Take(100))
            sb.AppendLine($"  - {entity.Name} ({entity.Type}): referenced in {entity.ChunkIds.Count} chunks");

        sb.AppendLine($"\nRelationships ({_relationships.Count}):");

        foreach (var rel in _relationships.Values.Take(100))
            sb.AppendLine($"  - {rel.Source} --[{rel.Predicate}]--> {rel.Target}");

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ExtractionResult(
        IReadOnlyList<GraphEntity> Entities,
        IReadOnlyList<GraphRelationship> Relationships);

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

    private sealed record GraphEntity
    {
        public required string Name { get; init; }
        public required string Type { get; init; }
        public required List<string> ChunkIds { get; init; }
    }

    private sealed record GraphRelationship
    {
        public required string Source { get; init; }
        public required string Predicate { get; init; }
        public required string Target { get; init; }
        public required string ChunkId { get; init; }
    }
}

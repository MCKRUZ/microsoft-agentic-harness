using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.KnowledgeGraph;

/// <summary>
/// Extracts entities and relationships from document chunks using LLM-based
/// structured extraction. For each chunk, prompts the LLM to identify named entities
/// and their relationships, then parses the JSON response into <see cref="GraphNode"/>
/// and <see cref="GraphEdge"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="IRagModelRouter"/> to route extraction to the economy-tier model,
/// since entity extraction is a high-volume operation where cost matters more than
/// maximum accuracy. The operation name <c>"graph_entity_extraction"</c> maps to the
/// economy tier by default.
/// </para>
/// <para>
/// Extraction failures for individual chunks are logged and skipped rather than
/// failing the entire batch. This ensures partial progress is preserved when the
/// LLM produces malformed JSON for some chunks.
/// </para>
/// </remarks>
public sealed class ExtractEntitiesExecutor : Executor<KgIngestionInput, ExtractedEntities>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<ExtractEntitiesExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtractEntitiesExecutor"/> class.
    /// </summary>
    /// <param name="modelRouter">Routes LLM calls to the appropriate model tier.</param>
    /// <param name="logger">Logger for recording extraction progress and failures.</param>
    public ExtractEntitiesExecutor(
        IRagModelRouter modelRouter,
        ILogger<ExtractEntitiesExecutor> logger)
        : base("extract_entities")
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(logger);

        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <summary>
    /// Processes each chunk through LLM entity extraction and aggregates the results.
    /// </summary>
    /// <param name="message">The ingestion input containing document chunks.</param>
    /// <param name="context">The workflow execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All extracted entities and relationships across the input chunks.</returns>
    public override async ValueTask<ExtractedEntities> HandleAsync(
        KgIngestionInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var client = _modelRouter.GetClientForOperation("graph_entity_extraction");
        var allNodes = new List<GraphNode>();
        var allEdges = new List<GraphEdge>();

        _logger.LogInformation(
            "Entity extraction started: {ChunkCount} chunks", message.Chunks.Count);

        foreach (var chunk in message.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (nodes, edges) = await ExtractFromChunkAsync(client, chunk, cancellationToken);
            allNodes.AddRange(nodes);
            allEdges.AddRange(edges);
        }

        _logger.LogInformation(
            "Entity extraction completed: {NodeCount} nodes, {EdgeCount} edges from {ChunkCount} chunks",
            allNodes.Count, allEdges.Count, message.Chunks.Count);

        return new ExtractedEntities(allNodes, allEdges, message.Chunks.Count, message.SourcePipeline);
    }

    private async Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)>
        ExtractFromChunkAsync(
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

            return (nodes, edges);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity extraction failed for chunk {ChunkId}", chunk.Id);
            return ([], []);
        }
    }

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

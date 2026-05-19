using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using Domain.AI.RAG.Enums;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Agent tool for searching indexed documents using the full RAG pipeline.
/// Supports hybrid vector+keyword retrieval, reranking, CRAG evaluation,
/// and optional GraphRAG for broad thematic queries.
/// </summary>
/// <remarks>
/// <para>
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;("document_search", (sp, _) =&gt;
///     new DocumentSearchTool(sp.GetRequiredService&lt;IRagOrchestrator&gt;()));
/// </code>
/// </para>
/// <para>
/// Operations:
/// <list type="bullet">
///   <item><c>"search"</c>: Basic search returning top results as formatted text. Accepts an optional
///   <c>strategy</c> parameter to override the default retrieval strategy at runtime. Accepted values:
///   <c>"hybrid"</c> (<see cref="RetrievalStrategy.HybridVectorBm25"/>),
///   <c>"graph"</c> or <c>"graphrag"</c> (<see cref="RetrievalStrategy.GraphRag"/>),
///   <c>"raptor"</c> (<see cref="RetrievalStrategy.RaptorTree"/>),
///   <c>"multiqueryFusion"</c> or <c>"fusion"</c> (<see cref="RetrievalStrategy.MultiQueryFusion"/>).
///   Unknown values fall back to the pipeline default.</item>
///   <item><c>"search_global"</c>: Forces <see cref="RetrievalStrategy.GraphRag"/> for broad thematic
///   queries. The <c>strategy</c> parameter is ignored — the operation-level override always wins.</item>
///   <item><c>"search_with_citations"</c>: Includes citation spans with source attribution.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class DocumentSearchTool : ITool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "document_search";

    private static readonly IReadOnlyList<string> Operations =
        ["search", "search_global", "search_with_citations"];

    private readonly IRagOrchestrator _orchestrator;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSearchTool"/> class.
    /// </summary>
    /// <param name="orchestrator">The RAG pipeline orchestrator.</param>
    public DocumentSearchTool(IRagOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Searches indexed documents using hybrid retrieval with vector and keyword search, " +
        "reranking, and optional GraphRAG for thematic queries.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return operation.ToLowerInvariant() switch
            {
                "search" => await SearchAsync(parameters, strategyOverride: null, cancellationToken),
                "search_global" => await SearchAsync(parameters, RetrievalStrategy.GraphRag, cancellationToken),
                "search_with_citations" => await SearchWithCitationsAsync(parameters, cancellationToken),
                _ => ToolResult.Fail(
                    $"Unknown operation: {operation}. Supported: {string.Join(", ", Operations)}")
            };
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Search operation was cancelled.");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private async Task<ToolResult> SearchAsync(
        IReadOnlyDictionary<string, object?> parameters,
        RetrievalStrategy? strategyOverride,
        CancellationToken cancellationToken)
    {
        var query = GetRequiredString(parameters, "query");
        var topK = GetOptionalInt(parameters, "top_k");
        var collection = GetOptionalString(parameters, "collection");

        // Allow agent to override strategy via parameter unless already overridden by the operation.
        var effectiveStrategy = strategyOverride ?? ParseStrategy(GetOptionalString(parameters, "strategy"));

        var context = await _orchestrator.SearchAsync(
            query, topK, collection, effectiveStrategy, cancellationToken);

        if (context.TotalTokens == 0)
            return ToolResult.Ok(context.AssembledText);

        var sb = new StringBuilder();
        sb.AppendLine(context.AssembledText);

        if (context.WasTruncated)
            sb.AppendLine("\n[Note: Results were truncated to fit the context budget.]");

        return ToolResult.Ok(sb.ToString());
    }

    private async Task<ToolResult> SearchWithCitationsAsync(
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var query = GetRequiredString(parameters, "query");
        var topK = GetOptionalInt(parameters, "top_k");
        var collection = GetOptionalString(parameters, "collection");

        var context = await _orchestrator.SearchAsync(
            query, topK, collection, cancellationToken: cancellationToken);

        var result = new
        {
            text = context.AssembledText,
            citations = context.Citations.Select(c => new
            {
                documentUri = c.DocumentUri.ToString(),
                sectionPath = c.SectionPath,
                startOffset = c.StartOffset,
                endOffset = c.EndOffset
            }),
            totalTokens = context.TotalTokens,
            wasTruncated = context.WasTruncated
        };

        return ToolResult.Ok(JsonSerializer.Serialize(result, JsonOptions));
    }

    /// <summary>
    /// Maps an agent-supplied strategy name to the corresponding
    /// <see cref="RetrievalStrategy"/>, or <see langword="null"/> when the value is
    /// unrecognised (preserving the pipeline default).
    /// </summary>
    private static RetrievalStrategy? ParseStrategy(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "hybrid" => RetrievalStrategy.HybridVectorBm25,
            "graph" or "graphrag" => RetrievalStrategy.GraphRag,
            "raptor" => RetrievalStrategy.RaptorTree,
            "multiqueryfusion" or "fusion" => RetrievalStrategy.MultiQueryFusion,
            _ => null
        };

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is not string s || string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"Required parameter '{key}' is missing or empty.");
        return s;
    }

    private static string? GetOptionalString(IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    private static int? GetOptionalInt(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value)) return null;
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

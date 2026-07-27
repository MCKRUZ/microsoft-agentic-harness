using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.Rag;

/// <summary>
/// Executes knowledge graph-based retrieval via <see cref="IGraphRagService.GlobalSearchAsync"/>.
/// This executor handles the <see cref="Domain.AI.RAG.Enums.RetrievalStrategy.GraphRag"/> branch
/// of the RAG workflow, bypassing vector retrieval entirely and producing an assembled context
/// directly from community-level map-reduce synthesis over the knowledge graph.
/// </summary>
/// <remarks>
/// GraphRAG excels at broad thematic queries (e.g., "What are the main themes in this corpus?")
/// where vector search performs poorly. The trade-off is higher indexing cost and the need
/// for a populated knowledge graph. When the graph is empty, returns a context indicating
/// that documents must be ingested first. When <paramref name="graphRagService"/> is null —
/// the service is registered only while the graph database backend is enabled — the executor
/// degrades the same way: an explanatory context naming the disabled backend, not an exception.
/// </remarks>
/// <param name="graphRagService">The graph retrieval service, or null when the graph database
/// backend is disabled (<c>AppConfig:AI:Rag:GraphDatabase:Enabled=false</c>).</param>
/// <param name="logger">Logger for recording search progress and degradations.</param>
public sealed class GraphRagSearchExecutor(
    IGraphRagService? graphRagService,
    ILogger<GraphRagSearchExecutor> logger)
    : Executor<ClassifiedQuery, RagAssembledContext>("graph_rag_search")
{
    private const int DefaultCommunityLevel = 0;

    /// <summary>
    /// Performs a global search over the knowledge graph and returns assembled context
    /// synthesized from community summaries.
    /// </summary>
    /// <param name="message">The classified query routed to the GraphRag strategy.</param>
    /// <param name="context">The workflow execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Assembled context from graph-based retrieval, ready for LLM prompt injection.</returns>
    public override async ValueTask<RagAssembledContext> HandleAsync(
        ClassifiedQuery message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (graphRagService is null)
        {
            logger.LogWarning(
                "GraphRag branch reached but the graph database backend is disabled " +
                "(AppConfig:AI:Rag:GraphDatabase:Enabled=false); returning explanatory context");
            return RagAssembledContext.GraphRagUnavailable();
        }

        logger.LogInformation("GraphRAG search started for query of {Length} chars", message.Query.Length);

        var result = await graphRagService.GlobalSearchAsync(
            message.Query, DefaultCommunityLevel, cancellationToken);

        logger.LogInformation(
            "GraphRAG search completed: {Tokens} tokens, truncated={Truncated}",
            result.TotalTokens, result.WasTruncated);

        return result;
    }
}

using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.Rag;

/// <summary>
/// Assembles the final RAG context from vector retrieval results. Runs pointer expansion,
/// citation tracking, and token budget enforcement via <see cref="IRagContextAssembler"/>
/// to produce a <see cref="RagAssembledContext"/> ready for LLM prompt injection.
/// </summary>
/// <remarks>
/// This executor sits at the end of the vector retrieval path. When the upstream
/// <see cref="VectorRetrievalExecutor"/> produces empty results (e.g., CRAG rejected
/// or retrieval returned nothing), this executor produces an empty context with
/// a descriptive message rather than failing.
/// </remarks>
public sealed class AssembleContextExecutor(
    IRagContextAssembler contextAssembler,
    ILogger<AssembleContextExecutor> logger)
    : Executor<VectorRetrievalOutput, RagAssembledContext>("assemble_context")
{
    private const int DefaultMaxTokens = 4096;

    /// <summary>
    /// Assembles reranked results into a token-budgeted context string with citations.
    /// </summary>
    /// <param name="message">The vector retrieval output containing ranked results.</param>
    /// <param name="context">The workflow execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembled context with citations and token accounting.</returns>
    public override async ValueTask<RagAssembledContext> HandleAsync(
        VectorRetrievalOutput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (message.RankedResults.Count == 0)
        {
            logger.LogWarning(
                "No results to assemble after {Attempts} attempt(s)", message.AttemptCount);

            return new RagAssembledContext
            {
                AssembledText = "No relevant documents found.",
                TotalTokens = 0,
                WasTruncated = false
            };
        }

        logger.LogInformation(
            "Assembling context from {Count} results (refined={Refined}, attempts={Attempts})",
            message.RankedResults.Count, message.WasRefined, message.AttemptCount);

        return await contextAssembler.AssembleAsync(
            message.RankedResults, DefaultMaxTokens, cancellationToken);
    }
}

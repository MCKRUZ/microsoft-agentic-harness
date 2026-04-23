using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Assembles final RAG context from reranked results. Runs pointer expansion,
/// tracks citations, enforces the token budget, and produces the assembled context
/// string ready for LLM consumption. This is the final stage of the RAG pipeline
/// before the assembled text is injected into the conversation prompt.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Processing order: pointer expansion (<see cref="IPointerExpander"/>) →
///         deduplication → budget enforcement → text formatting → citation tracking
///         (<see cref="ICitationTracker"/>).</item>
///   <item>Budget enforcement: Sort by rerank score descending and greedily include chunks
///         until the <paramref name="maxTokens"/> budget is exhausted. Set
///         <see cref="RagAssembledContext.WasTruncated"/> when chunks were dropped.</item>
///   <item>Text formatting: Separate chunks with configurable delimiters (e.g.,
///         <c>"\n---\n"</c>). Optionally prepend each chunk's <see cref="DocumentChunk.SectionPath"/>
///         as a heading for structural context.</item>
///   <item>Citation tracking: Call <see cref="ICitationTracker.Track"/> for each included
///         chunk with its offset in the assembled string. Attach the resulting citations to
///         the output <see cref="RagAssembledContext"/>.</item>
///   <item>Token counting: Use the tokenizer aligned with the generation model (not the
///         embedding model) for budget calculations.</item>
/// </list>
/// </remarks>
public interface IRagContextAssembler
{
    /// <summary>
    /// Assembles reranked results into a context string respecting the token budget.
    /// </summary>
    /// <param name="results">Reranked results to assemble, ordered by relevance.</param>
    /// <param name="maxTokens">
    /// Maximum token budget for the assembled context. The assembler includes chunks
    /// in rerank-score order until this budget is exhausted.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembled context with citations and token accounting.</returns>
    Task<RagAssembledContext> AssembleAsync(
        IReadOnlyList<RerankedResult> results,
        int maxTokens,
        CancellationToken cancellationToken = default);
}

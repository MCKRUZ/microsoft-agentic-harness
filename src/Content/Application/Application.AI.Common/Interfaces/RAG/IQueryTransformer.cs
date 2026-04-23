namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Transforms queries to improve retrieval quality. Implementations are registered
/// as keyed services by transformation strategy: <c>"rag_fusion"</c> (generates N
/// query variations for broader recall), <c>"hyde"</c> (generates a hypothetical
/// answer document for embedding-based retrieval), <c>"none"</c> (passthrough).
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item><c>"rag_fusion"</c>: Generates 3-5 rephrasings of the query using an LLM.
///         Each variation is embedded and searched independently; results are fused via RRF.
///         Best for ambiguous or underspecified queries.</item>
///   <item><c>"hyde"</c> (Hypothetical Document Embeddings): Generates a hypothetical answer
///         to the query using an LLM, then embeds the answer instead of the query. Works well
///         when the query and answer live in different semantic spaces (e.g., question vs.
///         documentation style).</item>
///   <item><c>"none"</c>: Returns the original query as a single-element list. Used when
///         transformation is disabled or the query classifier indicates a simple lookup.</item>
///   <item>Use <see cref="IRagModelRouter"/> to select an economy-tier model for generation —
///         query transformations don't need the best model.</item>
///   <item>The returned list always contains at least one query (the original or transformed).</item>
/// </list>
/// </remarks>
public interface IQueryTransformer
{
    /// <summary>
    /// Transforms a query into one or more retrieval-optimized variations.
    /// </summary>
    /// <param name="query">The original user query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One or more query strings to use for retrieval. The original query may or may not
    /// be included depending on the strategy. Always contains at least one element.
    /// </returns>
    Task<IReadOnlyList<string>> TransformAsync(
        string query,
        CancellationToken cancellationToken = default);
}

using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Generates hierarchical summaries using the RAPTOR pattern (Recursive Abstractive
/// Processing for Tree-Organized Retrieval). Clusters chunk embeddings at each level,
/// summarizes each cluster with an LLM, embeds the summaries, and repeats recursively.
/// The result is a multi-granularity representation: leaf chunks for detail queries,
/// intermediate summaries for thematic queries, and root summaries for broad questions.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Clustering: Use Gaussian Mixture Models (GMM) or k-means on chunk embeddings.
///         GMM is preferred because chunks can belong to multiple clusters (soft assignment).</item>
///   <item>Summarization: Use <see cref="IRagModelRouter"/> to select an economy-tier model —
///         this is a high-volume operation. Each cluster of 5-10 chunks produces one summary.</item>
///   <item>Recursion depth: <paramref name="maxDepth"/> controls how many levels of summaries
///         are generated. Typical values: 2-3. Depth 0 means no summarization.</item>
///   <item>The returned list includes both the original leaf chunks and all generated summary
///         chunks, each tagged with their RAPTOR level in metadata.</item>
///   <item>Summary chunks must be embedded before returning — callers expect embeddings
///         to be populated on all returned chunks.</item>
/// </list>
/// </remarks>
public interface IRaptorSummarizer
{
    /// <summary>
    /// Generates recursive summaries from the given chunks.
    /// </summary>
    /// <param name="chunks">
    /// The leaf-level chunks to summarize. Must have embeddings already populated.
    /// </param>
    /// <param name="maxDepth">
    /// Maximum recursion depth for summary generation. Each level clusters the previous
    /// level's outputs and produces a new set of summary chunks.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// All chunks including originals and generated summaries at each level,
    /// each with embeddings populated and RAPTOR level tagged in metadata.
    /// </returns>
    Task<IReadOnlyList<DocumentChunk>> SummarizeAsync(
        IReadOnlyList<DocumentChunk> chunks,
        int maxDepth,
        CancellationToken cancellationToken = default);
}

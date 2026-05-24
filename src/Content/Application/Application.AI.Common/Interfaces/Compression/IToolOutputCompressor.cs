using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Application.AI.Common.Interfaces.Compression;

/// <summary>
/// Orchestrates tool output compression by dispatching to the appropriate
/// <see cref="ICompressionStrategy"/> based on output category, applying
/// tiered fallback (heuristic → LLM → hard truncation).
/// </summary>
public interface IToolOutputCompressor
{
    /// <summary>
    /// Compresses tool output to fit within the token threshold.
    /// </summary>
    /// <param name="output">The raw tool output to compress.</param>
    /// <param name="category">
    /// The classified output category, or null to trigger automatic content detection.
    /// When null, the implementation uses ContentTypeDetector to infer the category.
    /// </param>
    /// <param name="tokenThreshold">Maximum token count for the result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The compression result. Always returns a valid result — never throws.
    /// On internal failure, returns hard-truncated output.
    /// </returns>
    Task<CompressionResult> CompressAsync(
        string output,
        ToolOutputCategory? category,
        int tokenThreshold,
        CancellationToken cancellationToken = default);
}

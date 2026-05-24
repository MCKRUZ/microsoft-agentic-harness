using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Application.AI.Common.Interfaces.Compression;

/// <summary>
/// Category-specific compression strategy for tool outputs.
/// Implementations are registered via keyed DI by <see cref="ToolOutputCategory"/>
/// and resolved by <see cref="IToolOutputCompressor"/> during compression.
/// </summary>
/// <remarks>
/// Strategies must be deterministic and side-effect-free (except
/// <c>FreeTextCompressionStrategy</c> which may call an LLM as fallback).
/// Return <c>CompressionResult.WasCompressed = false</c> to signal that
/// this strategy could not handle the content and the compressor should
/// fall through to the next strategy.
/// </remarks>
public interface ICompressionStrategy
{
    /// <summary>Returns whether this strategy can handle the given output category.</summary>
    bool CanHandle(ToolOutputCategory category);

    /// <summary>
    /// Compresses the output to fit within the token threshold.
    /// </summary>
    /// <param name="output">The tool output string to compress.</param>
    /// <param name="tokenThreshold">Target maximum token count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Compression result. Set <c>WasCompressed = false</c> if the strategy
    /// cannot handle this content (e.g., JSON parse failure) to signal fallthrough.
    /// </returns>
    Task<CompressionResult> CompressAsync(
        string output,
        int tokenThreshold,
        CancellationToken cancellationToken = default);
}

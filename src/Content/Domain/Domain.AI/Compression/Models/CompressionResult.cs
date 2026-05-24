namespace Domain.AI.Compression.Models;

/// <summary>
/// Outcome of compressing a tool output. Carries the compressed text,
/// token metrics, and which strategy produced the result.
/// </summary>
public sealed record CompressionResult
{
    /// <summary>The compressed (or original) output text.</summary>
    public required string Output { get; init; }

    /// <summary>Estimated token count of the original output.</summary>
    public required int OriginalTokens { get; init; }

    /// <summary>Estimated token count after compression.</summary>
    public required int CompressedTokens { get; init; }

    /// <summary>Name of the strategy that produced this result (e.g., "Json", "LlmFallback", "HardTruncate").</summary>
    public required string Strategy { get; init; }

    /// <summary>Whether compression was actually applied (false when output was below threshold).</summary>
    public required bool WasCompressed { get; init; }

    /// <summary>Creates a passthrough result for outputs below threshold.</summary>
    public static CompressionResult Passthrough(string output, int tokens) => new()
    {
        Output = output,
        OriginalTokens = tokens,
        CompressedTokens = tokens,
        Strategy = "None",
        WasCompressed = false
    };
}

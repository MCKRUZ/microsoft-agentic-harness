namespace Application.Common.Helpers;

/// <summary>
/// Stateless utilities for estimating token counts from text content.
/// Uses the standard heuristic of ~4 characters per token for English text,
/// which provides a reasonable approximation for context budget management.
/// </summary>
/// <remarks>
/// <para>
/// These estimates are not exact — actual tokenization depends on the model's
/// tokenizer (BPE, SentencePiece, etc.). For precise counts, use the model's
/// tokenizer library. These helpers are for budget estimation, skill loading
/// decisions, and context window planning where approximate counts suffice.
/// </para>
/// <para>
/// The ~4 chars/token ratio is well-established for GPT-family models on English text.
/// Non-English text, code, and structured data may have different ratios.
/// </para>
/// </remarks>
public static class TokenEstimationHelper
{
    /// <summary>Average characters per token for English text.</summary>
    private const int CharsPerToken = 4;

    /// <summary>
    /// Estimates the token count for a text string.
    /// </summary>
    /// <param name="text">The text to estimate. Returns 0 for null or empty.</param>
    /// <returns>The estimated token count.</returns>
    /// <example>
    /// <code>
    /// var tokens = TokenEstimationHelper.EstimateTokens("Hello, world!"); // ~3
    /// </code>
    /// </example>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharsPerToken - 1) / CharsPerToken;

    /// <summary>
    /// Estimates the total token count for multiple text segments.
    /// </summary>
    /// <param name="segments">The text segments to estimate.</param>
    /// <returns>The combined estimated token count.</returns>
    public static int EstimateTokens(IEnumerable<string?> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Sum(s => EstimateTokens(s));
    }

    /// <summary>
    /// Checks whether a text fits within a token budget.
    /// </summary>
    /// <param name="text">The text to check.</param>
    /// <param name="budgetTokens">The maximum allowed tokens.</param>
    /// <returns><c>true</c> if the estimated tokens fit within the budget.</returns>
    public static bool FitsWithinBudget(string? text, int budgetTokens) =>
        EstimateTokens(text) <= budgetTokens;

    /// <summary>
    /// Truncates text to fit within a token budget, appending an ellipsis indicator
    /// when truncation occurs.
    /// </summary>
    /// <param name="text">The text to potentially truncate.</param>
    /// <param name="maxTokens">The maximum allowed tokens.</param>
    /// <returns>The original text if it fits, or a truncated version with <c>...[truncated]</c>.</returns>
    public static string TruncateToTokenBudget(string? text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        const string suffix = "...[truncated]";
        var maxChars = maxTokens * CharsPerToken;

        if (text.Length <= maxChars)
            return text;

        var truncateAt = Math.Max(0, maxChars - suffix.Length);
        return string.Concat(text.AsSpan(0, truncateAt), suffix);
    }
}

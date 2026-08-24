namespace Application.AI.Common.Helpers;

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

    /// <summary>Nominal token cost of one tool's JSON schema.</summary>
    private const int TokensPerToolSchema = 50;

    /// <summary>
    /// Estimates the token cost of sending <paramref name="toolCount"/> tool JSON schemas to the model.
    /// </summary>
    /// <param name="toolCount">The number of tools whose schemas are sent. Negative counts estimate 0.</param>
    /// <returns>The estimated token count.</returns>
    /// <remarks>
    /// A flat per-schema figure rather than a measurement of the serialised schema: the schemas are built
    /// by the model client at request time and are not available to the harness when the budget is charged.
    /// It lives here so every site charging for tool schemas uses one number — two sites that each hardcode
    /// their own would drift silently, and the budget would still look plausible.
    /// </remarks>
    public static int EstimateToolSchemaTokens(int toolCount) =>
        toolCount <= 0 ? 0 : toolCount * TokensPerToolSchema;

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
    /// Estimates the token count for a span of text.
    /// </summary>
    /// <param name="text">The text to estimate. Returns 0 when empty.</param>
    /// <returns>The estimated token count.</returns>
    /// <remarks>
    /// For callers holding a slice of a larger string — measuring what was appended to a prompt, say.
    /// Materialising the slice just to be measured copies it for nothing, and on a per-turn path that is
    /// a copy of several kilobytes discarded immediately.
    /// </remarks>
    public static int EstimateTokens(ReadOnlySpan<char> text) =>
        text.IsEmpty ? 0 : (text.Length + CharsPerToken - 1) / CharsPerToken;

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
    /// Estimates the total token count for a list of chat messages.
    /// </summary>
    /// <param name="messages">The messages to estimate.</param>
    /// <returns>The combined estimated token count.</returns>
    /// <remarks>
    /// Accounts for every content item on each message, not just <c>ChatMessage.Text</c> — that
    /// property concatenates only <see cref="Microsoft.Extensions.AI.TextContent"/> and silently
    /// drops reasoning text, tool call arguments, and tool results. Tool payloads in particular are
    /// typically a conversation's largest context consumer, so estimating from <c>.Text</c> alone
    /// made the context-budget dashboard report the costliest category as free. Deliberately scoped
    /// to text-representable content: the chars-per-token heuristic this class uses throughout does
    /// not apply to image/binary content, whose token cost follows an entirely different model —
    /// estimating those here would be actively misleading, not just incomplete, so it is left at 0
    /// rather than guessed (<see cref="Microsoft.Extensions.AI.ImageGenerationToolResultContent"/>'s
    /// output is image data by definition, so it stays out on the same grounds). Every other
    /// <see cref="Microsoft.Extensions.AI.ToolResultContent"/> subtype the referenced SDK version
    /// ships — <see cref="Microsoft.Extensions.AI.FunctionResultContent"/>,
    /// <see cref="Microsoft.Extensions.AI.McpServerToolResultContent"/>,
    /// <see cref="Microsoft.Extensions.AI.WebSearchToolResultContent"/>,
    /// <see cref="Microsoft.Extensions.AI.CodeInterpreterToolResultContent"/> — carries a
    /// text-representable payload and is counted, even though only <c>FunctionResultContent</c> is
    /// exercised anywhere in this harness's tool surface today: leaving the other three at 0 would
    /// have reproduced the exact "costliest category is free" bug this method exists to close, for
    /// any consumer template that wires up a hosted MCP/web-search/code-interpreter tool later.
    /// </remarks>
    public static int EstimateTokens(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(m => EstimateContentListTokens(m.Contents));
    }

    /// <summary>Hard cap on how deeply a tool result's nested <c>Outputs</c> may recurse.</summary>
    /// <remarks>
    /// MCP tool results are an untrusted-boundary payload this harness does not control the shape
    /// of. Nesting is representable by type (<c>Outputs</c> items are themselves <see cref="Microsoft.Extensions.AI.AIContent"/>,
    /// which can itself carry an <c>Outputs</c> list) even though no known MCP content shape produces
    /// it today — a <see cref="StackOverflowException"/> from unbounded recursion is uncatchable and
    /// takes the whole process down, so this is cheap insurance against a payload shape this harness
    /// hasn't seen yet, not a response to a demonstrated one. Matches the order of magnitude of
    /// <c>System.Text.Json</c>'s own default <c>MaxDepth</c> (64), scaled down since tool-result
    /// nesting has no legitimate reason to run anywhere near that deep.
    /// </remarks>
    private const int MaxContentNestingDepth = 8;

    /// <summary>
    /// Estimates a list of <see cref="Microsoft.Extensions.AI.AIContent"/> items — a message's own
    /// content list, or a tool result's nested <c>Outputs</c> list (recursed into via
    /// <see cref="EstimateOutputsTokens"/>). Both shapes need the identical treatment: concatenate
    /// every <see cref="Microsoft.Extensions.AI.TextContent"/> fragment into one string before
    /// estimating it, rather than ceiling-rounding each fragment separately — a provider (or a
    /// nested tool result) commonly splits one logical block of text into several
    /// <see cref="Microsoft.Extensions.AI.TextContent"/> items, and per-fragment rounding would
    /// overcount purely from how the content happened to be chunked, not from anything it costs.
    /// </summary>
    private static int EstimateContentListTokens(
        IEnumerable<Microsoft.Extensions.AI.AIContent> contents, int depth = 0)
    {
        if (depth >= MaxContentNestingDepth)
            return 0;

        var list = contents as IReadOnlyCollection<Microsoft.Extensions.AI.AIContent> ?? contents.ToList();

        var text = string.Concat(
            list.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        var tokens = EstimateTokens(text);

        foreach (var content in list)
        {
            tokens += content switch
            {
                Microsoft.Extensions.AI.TextContent => 0, // already folded into `text` above
                // Reasoning/thinking output (Claude extended thinking, OpenAI o-series) is real,
                // separately-billed text a connector can populate without any extra harness config.
                Microsoft.Extensions.AI.TextReasoningContent reasoning => EstimateTokens(reasoning.Text),
                Microsoft.Extensions.AI.FunctionCallContent call => EstimateTokens(call.Name) +
                    (call.Arguments is { Count: > 0 } args ? TrySerializeAndEstimate(args) : 0),
                Microsoft.Extensions.AI.FunctionResultContent result => EstimateResultTokens(result.Result),
                Microsoft.Extensions.AI.McpServerToolResultContent mcp =>
                    EstimateOutputsTokens(mcp.Outputs, depth),
                Microsoft.Extensions.AI.WebSearchToolResultContent web =>
                    EstimateOutputsTokens(web.Outputs, depth),
                Microsoft.Extensions.AI.CodeInterpreterToolResultContent code =>
                    EstimateOutputsTokens(code.Outputs, depth),
                // ImageGenerationToolResultContent and anything else: image/binary content the
                // chars-per-token heuristic does not apply to, or a type this harness doesn't
                // recognize yet — left at 0 rather than guessed.
                _ => 0,
            };
        }

        return tokens;
    }

    private static int EstimateOutputsTokens(IList<Microsoft.Extensions.AI.AIContent>? outputs, int depth) =>
        outputs is null ? 0 : EstimateContentListTokens(outputs, depth + 1);

    private static int EstimateResultTokens(object? result) => result switch
    {
        null => 0,
        // The common case — most tool implementations in this codebase already return pre-serialized
        // JSON text — is measured directly with no serialization cost.
        string text => EstimateTokens(text),
        // A non-string Result (a POCO, JsonElement, or Dictionary returned directly) is what the
        // framework itself serializes to JSON before it reaches the wire. Calling .ToString() on it
        // instead would measure the CLR type name, not the payload — silently undercounting exactly
        // the category (tool results) this method exists to stop under-reporting.
        _ => TrySerializeAndEstimate(result),
    };

    private static int TrySerializeAndEstimate(object value)
    {
        try
        {
            return EstimateTokens(System.Text.Json.JsonSerializer.Serialize(value));
        }
        catch (Exception)
        {
            // Broad by design: System.Text.Json.JsonSerializer can throw NotSupportedException for
            // an unsupported CLR type, not only JsonException — a budget estimate degrading to 0 for
            // one unserializable value is preferable to throwing out of a context-budget computation
            // the caller cannot recover from mid-turn. Silent, unlike the structurally similar
            // serialize-with-fallback at ToolDiagnosticsMiddleware.LogToolCallsInResponse, which logs
            // via its injected ILogger: this class is a dependency-free static utility by design (used
            // from hot paths with no logger in scope), so there is nothing to log through here. An
            // unexpectedly-low token estimate is the only externally visible symptom of this path
            // firing — worth checking here first if a budget/compaction number looks implausible.
            return 0;
        }
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

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);

        const string suffix = "...[truncated]";
        var maxChars = maxTokens * CharsPerToken;

        if (text.Length <= maxChars)
            return text;

        var truncateAt = Math.Max(0, maxChars - suffix.Length);
        return string.Concat(text.AsSpan(0, truncateAt), suffix);
    }
}

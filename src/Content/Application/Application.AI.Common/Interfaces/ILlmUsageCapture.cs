namespace Application.AI.Common.Interfaces;

/// <summary>
/// Scoped service that accumulates LLM token usage across multiple chat client
/// calls within a single agent turn. The <see cref="Middleware.ObservabilityMiddleware"/>
/// records usage after each call; handlers read the accumulated totals via
/// <see cref="TakeSnapshot"/> after <c>agent.RunAsync()</c> completes.
/// </summary>
public interface ILlmUsageCapture
{
    /// <summary>
    /// Records token usage from a single LLM call. Called by middleware after each
    /// <c>GetResponseAsync</c>. Accumulates across multiple calls within a turn.
    /// </summary>
    void Record(int inputTokens, int outputTokens, int cacheRead, int cacheWrite, string? model);

    /// <summary>
    /// Records a tool invocation by name. Called by middleware when the LLM requests
    /// a function call. Accumulates distinct tool names within a turn.
    /// </summary>
    void RecordToolCall(string toolName);

    /// <summary>
    /// Records the LLM's tool-call request including the call id and serialized
    /// arguments. Pairs later with <see cref="RecordToolResult"/> via <paramref name="callId"/>
    /// so the per-tool args + stdout land on a single observability row.
    /// </summary>
    /// <param name="callId">LLM-supplied call id from <c>FunctionCallContent.CallId</c>.</param>
    /// <param name="toolName">Tool name.</param>
    /// <param name="argsJson">Serialized arguments. May be null when args were unparseable.</param>
    void RecordToolRequest(string? callId, string toolName, string? argsJson);

    /// <summary>
    /// Records the function-result payload that was sent back to the LLM. Matched
    /// to the prior <see cref="RecordToolRequest"/> by <paramref name="callId"/>.
    /// </summary>
    /// <param name="callId">LLM-supplied call id from <c>FunctionResultContent.CallId</c>.</param>
    /// <param name="stdout">Serialized result. May be truncated by middleware.</param>
    void RecordToolResult(string? callId, string? stdout);

    /// <summary>
    /// Returns the accumulated usage since the last snapshot and resets counters.
    /// Call before <c>agent.RunAsync()</c> to clear stale data, then again after
    /// to capture the turn's totals.
    /// </summary>
    LlmUsageSnapshot TakeSnapshot();
}

/// <summary>
/// One captured tool invocation pair: the LLM's call request (name + args) and
/// the subsequent function-result payload returned to the model. Either side may
/// be missing for partial turns (e.g. tool call without observed result).
/// </summary>
public record ToolInvocationCapture(
    string? CallId,
    string ToolName,
    string? ArgsJson,
    string? Stdout);

/// <summary>
/// Token usage from a single LLM call, in the order <see cref="ILlmUsageCapture.Record"/> saw it.
/// Unlike <see cref="LlmUsageSnapshot"/>'s totals — accumulated across every call in the turn — this
/// is what one specific call's prompt actually cost, which is what a context-bar reconciliation
/// needs to compare against (#517): the accumulated total answers "how much did this turn cost
/// altogether," not "how big was the context window for the call that just ran."
/// </summary>
public record LlmCallUsage(
    int InputTokens,
    int OutputTokens,
    int CacheRead,
    int CacheWrite,
    string? Model);

/// <summary>
/// Immutable snapshot of accumulated LLM usage for a single agent turn.
/// </summary>
public record LlmUsageSnapshot(
    int InputTokens,
    int OutputTokens,
    int CacheRead,
    int CacheWrite,
    string? Model,
    decimal CostUsd,
    decimal CacheHitPct,
    IReadOnlyList<string> ToolNames)
{
    /// <summary>
    /// Per-CallId tool invocation captures (args + stdout) collected by the
    /// ToolDiagnostics middleware. Empty when no tool dispatch happened.
    /// </summary>
    public IReadOnlyList<ToolInvocationCapture> ToolInvocations { get; init; } =
        Array.Empty<ToolInvocationCapture>();

    /// <summary>
    /// Each individual model call's usage, in call order, alongside the totals above that sum them.
    /// Empty when no call landed this turn (e.g. a turn that failed before any LLM call completed).
    /// The last entry is the call whose prompt the context bar's per-turn reconciliation compares
    /// against (#517) — the most recent model call is the closest available measurement of "the
    /// context window as it stood for the call that just ran."
    /// </summary>
    public IReadOnlyList<LlmCallUsage> Calls { get; init; } = Array.Empty<LlmCallUsage>();

    /// <summary>
    /// The turn's last model call's own prompt size — input, cache-read, and cache-write tokens for
    /// that one call, all three real prompt content (<c>LlmCostCalculator</c> prices them identically).
    /// This is the figure a context-bar reconciliation needs (#517), never <see cref="InputTokens"/>
    /// above, which accumulates across every call in the turn and answers "what did the whole turn
    /// cost," not "how big was the context window for the call that just ran." Null when no call
    /// landed this turn.
    /// </summary>
    public int? LastCallPromptTokens =>
        Calls.Count > 0
            ? Calls[^1].InputTokens + Calls[^1].CacheRead + Calls[^1].CacheWrite
            : null;
}

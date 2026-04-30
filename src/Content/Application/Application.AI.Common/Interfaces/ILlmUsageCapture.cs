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
    /// Returns the accumulated usage since the last snapshot and resets counters.
    /// Call before <c>agent.RunAsync()</c> to clear stale data, then again after
    /// to capture the turn's totals.
    /// </summary>
    LlmUsageSnapshot TakeSnapshot();
}

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
    IReadOnlyList<string> ToolNames);

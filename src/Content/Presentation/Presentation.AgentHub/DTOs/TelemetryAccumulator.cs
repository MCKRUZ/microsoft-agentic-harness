namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Running totals for session-level telemetry. Persisted on the <see cref="ConversationRecord"/>
/// so the stateless AG-UI handler can accumulate metrics across HTTP requests.
/// </summary>
public sealed record TelemetryAccumulator(
    int TurnCount,
    int ToolCallCount,
    int InputTokens,
    int OutputTokens,
    int CacheRead,
    int CacheWrite,
    decimal CostUsd)
{
    /// <summary>Empty accumulator — starting point for a new session.</summary>
    public static readonly TelemetryAccumulator Zero = new(0, 0, 0, 0, 0, 0, 0m);

    /// <summary>Returns a new accumulator with this turn's usage added.</summary>
    public TelemetryAccumulator Add(int inputTokens, int outputTokens, int cacheRead, int cacheWrite, decimal costUsd, int toolCalls) =>
        new(TurnCount + 1, ToolCallCount + toolCalls,
            InputTokens + inputTokens, OutputTokens + outputTokens,
            CacheRead + cacheRead, CacheWrite + cacheWrite,
            CostUsd + costUsd);

    /// <summary>Ratio of cache-read tokens to total input tokens (0..1).</summary>
    public decimal CacheHitRate
    {
        get
        {
            var total = InputTokens + CacheRead;
            return total > 0 ? (decimal)CacheRead / total : 0m;
        }
    }
}

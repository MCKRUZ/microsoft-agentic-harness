namespace Application.AI.Common.Models.Conversations;

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

    /// <summary>
    /// Returns what has been added since <paramref name="baseline"/> — this accumulator minus that one,
    /// field by field.
    /// </summary>
    /// <param name="baseline">An earlier snapshot of the same accumulator.</param>
    /// <returns>The difference between the two.</returns>
    /// <remarks>
    /// Lets a caller that needs both "everything so far" and "my share of it" keep one running total and
    /// derive the other, rather than accumulating the same events twice. Two counters over one event
    /// stream are two chances to disagree, which is precisely the defect in issue #255.
    /// </remarks>
    public TelemetryAccumulator Since(TelemetryAccumulator baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        return new(
            TurnCount - baseline.TurnCount,
            ToolCallCount - baseline.ToolCallCount,
            InputTokens - baseline.InputTokens,
            OutputTokens - baseline.OutputTokens,
            CacheRead - baseline.CacheRead,
            CacheWrite - baseline.CacheWrite,
            CostUsd - baseline.CostUsd);
    }

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

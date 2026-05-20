using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Orchestration;

/// <summary>
/// Thread-safe retrieval cost tracker using <see cref="Interlocked"/> operations
/// for lock-free concurrent recording. Produces an aggregated cost summary
/// with estimated USD cost based on configured token pricing.
/// </summary>
public sealed class RetrievalCostTracker : IRetrievalCostTracker
{
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    private long _promptTokens;
    private long _completionTokens;
    private long _retrievalCalls;
    private long _totalLatencyTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrievalCostTracker"/> class.
    /// </summary>
    /// <param name="configMonitor">Application configuration for token pricing.</param>
    public RetrievalCostTracker(IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(configMonitor);
        _configMonitor = configMonitor;
    }

    /// <inheritdoc />
    public void RecordCall(int promptTokens, int completionTokens, TimeSpan latency)
    {
        Interlocked.Add(ref _promptTokens, promptTokens);
        Interlocked.Add(ref _completionTokens, completionTokens);
        Interlocked.Increment(ref _retrievalCalls);
        Interlocked.Add(ref _totalLatencyTicks, latency.Ticks);
    }

    /// <inheritdoc />
    public RetrievalCostSummary GetSummary()
    {
        var promptTokens = Interlocked.Read(ref _promptTokens);
        var completionTokens = Interlocked.Read(ref _completionTokens);
        var calls = Interlocked.Read(ref _retrievalCalls);
        var latencyTicks = Interlocked.Read(ref _totalLatencyTicks);

        var multiSourceConfig = _configMonitor.CurrentValue.AI.Rag.MultiSource;
        var costPerInputToken = multiSourceConfig.CostPerMillionInputTokens / 1_000_000.0;
        var costPerOutputToken = multiSourceConfig.CostPerMillionOutputTokens / 1_000_000.0;

        var estimatedCost = (promptTokens * costPerInputToken) + (completionTokens * costPerOutputToken);

        return new RetrievalCostSummary
        {
            TotalTokensUsed = (int)(promptTokens + completionTokens),
            PromptTokens = (int)promptTokens,
            CompletionTokens = (int)completionTokens,
            RetrievalCalls = (int)calls,
            TotalLatency = TimeSpan.FromTicks(latencyTicks),
            EstimatedCost = estimatedCost
        };
    }

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Exchange(ref _promptTokens, 0);
        Interlocked.Exchange(ref _completionTokens, 0);
        Interlocked.Exchange(ref _retrievalCalls, 0);
        Interlocked.Exchange(ref _totalLatencyTicks, 0);
    }
}

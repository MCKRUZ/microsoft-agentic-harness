using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Context;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Context;

/// <summary>
/// Thread-safe tracker for per-agent token allocations across context components.
/// Uses <see cref="ConcurrentDictionary{TKey, TValue}"/> for lock-free concurrent access
/// when multiple agents execute simultaneously.
/// </summary>
/// <remarks>
/// Logs a warning when an agent's budget consumption exceeds 80%, giving the orchestration
/// loop an opportunity to compact or shed context before hitting the hard limit.
/// </remarks>
public sealed class ContextBudgetTracker : IContextBudgetTracker
{
    private const double WarningThreshold = 0.80;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _allocations = new();
    private readonly ILogger<ContextBudgetTracker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextBudgetTracker"/> class.
    /// </summary>
    /// <param name="logger">Logger for budget warnings and diagnostics.</param>
    public ContextBudgetTracker(ILogger<ContextBudgetTracker> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void RecordAllocation(string agentName, string component, int tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);

        var agentAllocations = _allocations.GetOrAdd(agentName, _ => new ConcurrentDictionary<string, int>());
        agentAllocations.AddOrUpdate(component, tokens, (_, existing) => existing + tokens);

        _logger.LogDebug(
            "Recorded {Tokens} tokens for agent {AgentName} component {Component}",
            tokens, agentName, component);
    }

    /// <inheritdoc />
    public int GetTotalAllocated(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        if (!_allocations.TryGetValue(agentName, out var agentAllocations))
            return 0;

        return agentAllocations.Values.Sum();
    }

    /// <inheritdoc />
    public int GetRemainingBudget(string agentName, int totalBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBudget);

        var used = GetTotalAllocated(agentName);
        var remaining = Math.Max(0, totalBudget - used);

        if (totalBudget > 0)
        {
            var utilizationRatio = (double)used / totalBudget;
            if (utilizationRatio >= WarningThreshold)
            {
                _logger.LogWarning(
                    "Agent {AgentName} budget at {Utilization:P0}: {Used:N0}/{Total:N0} tokens used, {Remaining:N0} remaining",
                    agentName, utilizationRatio, used, totalBudget, remaining);
            }
        }

        return remaining;
    }

    /// <inheritdoc />
    public void EnsureBudget(string agentName, int additionalTokens, int totalBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBudget);

        var currentUsed = GetTotalAllocated(agentName);
        var projectedTotal = currentUsed + additionalTokens;

        if (projectedTotal > totalBudget)
        {
            _logger.LogError(
                "Agent {AgentName} would exceed budget: {Projected:N0} tokens projected vs {Limit:N0} limit",
                agentName, projectedTotal, totalBudget);

            throw new ContextBudgetExceededException(totalBudget, projectedTotal, agentName);
        }
    }

    /// <inheritdoc />
    public void Reset(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        if (_allocations.TryRemove(agentName, out _))
        {
            _logger.LogInformation("Reset budget tracking for agent {AgentName}", agentName);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> GetBreakdown(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        if (!_allocations.TryGetValue(agentName, out var agentAllocations))
            return new Dictionary<string, int>();

        return new Dictionary<string, int>(agentAllocations);
    }
}

using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Context;

/// <summary>
/// Thread-safe tracker for per-agent token allocations across context components.
/// Uses <see cref="ConcurrentDictionary{TKey, TValue}"/> for lock-free concurrent access
/// when multiple agents execute simultaneously.
/// </summary>
/// <remarks>
/// Logs a warning when an agent's budget consumption exceeds 80%, giving the orchestration
/// loop an opportunity to compact or shed context before hitting the hard limit.
/// Also tracks continuation deltas for diminishing returns detection.
/// </remarks>
public sealed class ContextBudgetTracker : IContextBudgetTracker
{
    private const double WarningThreshold = 0.80;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _allocations = new();
    private readonly ConcurrentDictionary<string, ContinuationState> _continuationState = new();
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ContextBudgetTracker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextBudgetTracker"/> class.
    /// </summary>
    /// <param name="options">Application configuration for budget thresholds.</param>
    /// <param name="logger">Logger for budget warnings and diagnostics.</param>
    public ContextBudgetTracker(IOptionsMonitor<AppConfig> options, ILogger<ContextBudgetTracker> logger)
    {
        _options = options;
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

        var allocationsRemoved = _allocations.TryRemove(agentName, out _);
        var continuationRemoved = _continuationState.TryRemove(agentName, out _);

        if (allocationsRemoved || continuationRemoved)
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

    /// <inheritdoc />
    public BudgetAssessment AssessContinuation(string agentName, int totalBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBudget);

        var budgetConfig = _options.CurrentValue.AI.ContextManagement.Budget;
        var totalAllocated = GetTotalAllocated(agentName);
        var completionPercentage = totalBudget > 0 ? (double)totalAllocated / totalBudget : 0.0;
        var state = _continuationState.GetValueOrDefault(agentName, new ContinuationState());

        if (completionPercentage >= budgetConfig.CompletionThresholdRatio)
        {
            return new BudgetAssessment
            {
                Action = TokenBudgetAction.Stop,
                Reason = $"Budget {completionPercentage:P0} consumed — at or above {budgetConfig.CompletionThresholdRatio:P0} threshold",
                ContinuationCount = state.ContinuationCount,
                CompletionPercentage = completionPercentage
            };
        }

        if (state.ContinuationCount >= budgetConfig.DiminishingReturnsContinuationThreshold
            && state.LastDelta < budgetConfig.DiminishingReturnsMinDelta
            && state.PreviousDelta < budgetConfig.DiminishingReturnsMinDelta)
        {
            return new BudgetAssessment
            {
                Action = TokenBudgetAction.Stop,
                Reason = $"Diminishing returns: last two deltas ({state.PreviousDelta}, {state.LastDelta}) below {budgetConfig.DiminishingReturnsMinDelta} threshold after {state.ContinuationCount} continuations",
                ContinuationCount = state.ContinuationCount,
                CompletionPercentage = completionPercentage
            };
        }

        if (completionPercentage >= WarningThreshold)
        {
            return new BudgetAssessment
            {
                Action = TokenBudgetAction.Nudge,
                Reason = $"Budget {completionPercentage:P0} consumed — approaching limit",
                ContinuationCount = state.ContinuationCount,
                CompletionPercentage = completionPercentage
            };
        }

        return new BudgetAssessment
        {
            Action = TokenBudgetAction.Continue,
            Reason = "Budget and progress healthy",
            ContinuationCount = state.ContinuationCount,
            CompletionPercentage = completionPercentage
        };
    }

    /// <inheritdoc />
    public void RecordContinuation(string agentName, int tokensProduced)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentOutOfRangeException.ThrowIfNegative(tokensProduced);

        var updated = _continuationState.AddOrUpdate(
            agentName,
            _ => new ContinuationState
            {
                ContinuationCount = 1,
                LastDelta = tokensProduced,
                PreviousDelta = 0
            },
            (_, existing) => new ContinuationState
            {
                ContinuationCount = existing.ContinuationCount + 1,
                LastDelta = tokensProduced,
                PreviousDelta = existing.LastDelta
            });

        _logger.LogDebug(
            "Agent {AgentName} continuation #{Count}: {TokensProduced} tokens",
            agentName,
            updated.ContinuationCount,
            tokensProduced);
    }

    private sealed record ContinuationState
    {
        public int ContinuationCount { get; init; }
        public int LastDelta { get; init; }
        public int PreviousDelta { get; init; }
    }
}

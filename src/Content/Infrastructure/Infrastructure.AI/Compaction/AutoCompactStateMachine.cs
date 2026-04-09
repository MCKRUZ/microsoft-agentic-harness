using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Compaction;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Compaction;

/// <summary>
/// Thread-safe circuit breaker state machine for auto-compaction.
/// Tracks consecutive failures per agent and trips the breaker when
/// the configured threshold is reached. Resets after a cooldown period.
/// </summary>
public sealed class AutoCompactStateMachine : IAutoCompactStateMachine
{
    private readonly ConcurrentDictionary<string, AgentCompactionState> _states = new();
    private readonly IOptionsMonitor<AppConfig> _options;

    /// <summary>
    /// Initializes a new instance of <see cref="AutoCompactStateMachine"/>.
    /// </summary>
    /// <param name="options">Application configuration containing circuit breaker thresholds.</param>
    public AutoCompactStateMachine(IOptionsMonitor<AppConfig> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public void RecordSuccess(string agentId)
    {
        _states.AddOrUpdate(
            agentId,
            _ => new AgentCompactionState(0, null),
            (_, _) => new AgentCompactionState(0, null));
    }

    /// <inheritdoc />
    public void RecordFailure(string agentId)
    {
        _states.AddOrUpdate(
            agentId,
            _ => new AgentCompactionState(1, DateTimeOffset.UtcNow),
            (_, existing) => new AgentCompactionState(
                existing.ConsecutiveFailures + 1,
                DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    public bool IsCircuitBroken(string agentId)
    {
        if (!_states.TryGetValue(agentId, out var state))
            return false;

        var config = _options.CurrentValue.AI.ContextManagement.Compaction;

        if (state.ConsecutiveFailures < config.CircuitBreakerMaxFailures)
            return false;

        if (state.LastFailure is null)
            return false;

        var elapsed = DateTimeOffset.UtcNow - state.LastFailure.Value;
        return elapsed.TotalSeconds < config.CircuitBreakerCooldownSeconds;
    }

    /// <inheritdoc />
    public int GetConsecutiveFailures(string agentId)
    {
        return _states.TryGetValue(agentId, out var state)
            ? state.ConsecutiveFailures
            : 0;
    }

    /// <summary>
    /// Internal state record for an agent's compaction circuit breaker.
    /// </summary>
    private sealed record AgentCompactionState(
        int ConsecutiveFailures,
        DateTimeOffset? LastFailure);
}

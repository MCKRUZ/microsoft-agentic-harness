// src/Content/Infrastructure/Infrastructure.AI/Routing/EscalationTracker.cs
using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Routing;

/// <summary>
/// In-memory per-conversation escalation state machine.
/// Tracks quality signals and adjusts effective model tier accordingly.
/// </summary>
public sealed class EscalationTracker : IEscalationTracker
{
    private readonly ConcurrentDictionary<string, ConversationEscalationState> _states = new();
    private readonly ModelRoutingConfig _config;
    private readonly ILogger<EscalationTracker> _logger;

    public EscalationTracker(
        IOptions<ModelRoutingConfig> config,
        ILogger<EscalationTracker> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ModelTier GetEffectiveTier(
        string conversationId,
        TaskComplexity baseComplexity,
        IReadOnlyList<ModelTier> availableTiers)
    {
        var orderedTiers = availableTiers.OrderBy(t => t.EstimatedCostPer1KTokens).ToList();
        var baseTierIndex = GetTierIndexForComplexity(baseComplexity, orderedTiers);

        if (!_config.Escalation.Enabled || !_states.TryGetValue(conversationId, out var state))
            return orderedTiers[baseTierIndex];

        var effectiveIndex = Math.Min(baseTierIndex + state.EscalationLevel, orderedTiers.Count - 1);
        return orderedTiers[effectiveIndex];
    }

    /// <inheritdoc />
    public void RecordOutcome(string conversationId, TurnOutcome outcome)
    {
        var state = _states.GetOrAdd(conversationId, _ => new ConversationEscalationState());

        if (IsNegativeOutcome(outcome))
        {
            state.ConsecutiveNegatives++;
            state.EscalationLevel = Math.Min(state.ConsecutiveNegatives, 2);
            state.SuccessesSinceEscalation = 0;

            _logger.LogDebug(
                "Escalation bump for {ConversationId}: {Outcome}, level now {Level}",
                conversationId, outcome, state.EscalationLevel);
        }
        else if (outcome == TurnOutcome.Success && state.EscalationLevel > 0)
        {
            state.ConsecutiveNegatives = 0;
            state.SuccessesSinceEscalation++;

            if (state.SuccessesSinceEscalation > _config.Escalation.CooldownTurns)
            {
                state.EscalationLevel = Math.Max(state.EscalationLevel - 1, 0);
                state.SuccessesSinceEscalation = 0;

                _logger.LogDebug(
                    "Escalation downshift for {ConversationId}: level now {Level}",
                    conversationId, state.EscalationLevel);
            }
        }
        else if (outcome == TurnOutcome.Success)
        {
            state.ConsecutiveNegatives = 0;
        }
    }

    /// <inheritdoc />
    public void Reset(string conversationId)
    {
        _states.TryRemove(conversationId, out _);
    }

    private static int GetTierIndexForComplexity(TaskComplexity complexity, IReadOnlyList<ModelTier> orderedTiers)
    {
        if (orderedTiers.Count == 0) return 0;

        return complexity switch
        {
            TaskComplexity.Trivial => 0,
            TaskComplexity.Simple => 0,
            TaskComplexity.Moderate => Math.Min(1, orderedTiers.Count - 1),
            TaskComplexity.Complex => Math.Min(2, orderedTiers.Count - 1),
            _ => Math.Min(1, orderedTiers.Count - 1)
        };
    }

    private static bool IsNegativeOutcome(TurnOutcome outcome) =>
        outcome is TurnOutcome.UserCorrection or TurnOutcome.RetryRequested
            or TurnOutcome.ToolFailure or TurnOutcome.Timeout;

    private sealed class ConversationEscalationState
    {
        public int EscalationLevel { get; set; }
        public int ConsecutiveNegatives { get; set; }
        public int SuccessesSinceEscalation { get; set; }
    }
}

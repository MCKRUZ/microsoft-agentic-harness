// src/Content/Infrastructure/Infrastructure.AI/Routing/ModelRouter.cs
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Routing;

/// <summary>
/// Unified model router orchestrating heuristic classification, LLM fallback,
/// escalation tracking, and client resolution for all model selection decisions.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly ITaskComplexityHeuristic _heuristic;
    private readonly IServiceProvider _serviceProvider;
    private readonly IEscalationTracker _escalationTracker;
    private readonly IChatClientFactory _clientFactory;
    private readonly ModelRoutingConfig _config;
    private readonly IReadOnlyList<ModelTier> _orderedTiers;
    private readonly ILogger<ModelRouter> _logger;
    private ITaskComplexityClassifier? _classifierInstance;

    /// <summary>
    /// Lazily resolves the classifier to break the circular dependency
    /// ModelRouter → ITaskComplexityClassifier → IModelRouter.
    /// </summary>
    private ITaskComplexityClassifier Classifier =>
        _classifierInstance ??= _serviceProvider.GetRequiredService<ITaskComplexityClassifier>();

    /// <summary>
    /// Initializes the router, building a cost-ordered tier list from config.
    /// </summary>
    public ModelRouter(
        ITaskComplexityHeuristic heuristic,
        IServiceProvider serviceProvider,
        IEscalationTracker escalationTracker,
        IChatClientFactory clientFactory,
        IOptions<ModelRoutingConfig> config,
        ILogger<ModelRouter> logger)
    {
        _heuristic = heuristic;
        _serviceProvider = serviceProvider;
        _escalationTracker = escalationTracker;
        _clientFactory = clientFactory;
        _config = config.Value;
        _logger = logger;

        _orderedTiers = _config.Tiers
            .OrderBy(t => t.EstimatedCostPer1KTokens)
            .Select(t => new ModelTier
            {
                Name = t.Name,
                ClientType = t.ClientType,
                DeploymentName = t.DeploymentName,
                FallbackChainName = t.FallbackChainName,
                MaxTokensPerMinute = t.MaxTokensPerMinute,
                EstimatedCostPer1KTokens = t.EstimatedCostPer1KTokens
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ModelRoutingDecision> RouteAgentTurnAsync(
        AgentTurnContext turnContext,
        CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Routing disabled — using default tier {Tier}", _config.DefaultTier);
            return await BuildDecisionForTierAsync(
                GetDefaultTier(), TaskComplexity.Moderate, ClassificationSource.Heuristic, 1.0, "Routing disabled", false, ct);
        }

        var assessment = _heuristic.Classify(turnContext)
            ?? await Classifier.ClassifyAsync(turnContext, ct);

        var effectiveTier = _escalationTracker.GetEffectiveTier(turnContext.ConversationId, assessment.Complexity, _orderedTiers);
        var baseTier = GetBaseTierForComplexity(assessment.Complexity);
        var wasEscalated = !effectiveTier.Name.Equals(baseTier.Name, StringComparison.OrdinalIgnoreCase);

        _logger.LogDebug(
            "Routing turn {Turn} in {Conversation}: complexity={Complexity} source={Source} tier={Tier} escalated={Escalated}",
            turnContext.TurnNumber, turnContext.ConversationId, assessment.Complexity, assessment.Source, effectiveTier.Name, wasEscalated);

        return await BuildDecisionForTierAsync(effectiveTier, assessment.Complexity, assessment.Source, assessment.Confidence, assessment.Reasoning, wasEscalated, ct);
    }

    /// <inheritdoc />
    public async Task<ModelRoutingDecision> RouteOperationAsync(
        string operationName,
        CancellationToken ct = default)
    {
        var tierName = _config.OperationOverrides.TryGetValue(operationName, out var overrideTier)
            ? overrideTier
            : _config.DefaultTier;

        var tier = _orderedTiers.FirstOrDefault(t => t.Name.Equals(tierName, StringComparison.OrdinalIgnoreCase))
            ?? GetDefaultTier();

        _logger.LogDebug("Routing operation {Operation} to tier {Tier}", operationName, tier.Name);

        return await BuildDecisionForTierAsync(
            tier, TaskComplexity.Moderate, ClassificationSource.Heuristic, 1.0, $"Operation override: {operationName}", false, ct);
    }

    /// <inheritdoc />
    public async Task<TaskComplexityAssessment> AssessTaskComplexityAsync(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        CancellationToken ct = default)
    {
        var context = new AgentTurnContext
        {
            ConversationId = "supervisor-assessment",
            UserMessage = taskDescription,
            TurnNumber = 1,
            AvailableToolCount = requiredCapabilities.Count,
            RecentToolNames = requiredCapabilities
        };

        return _heuristic.Classify(context)
            ?? await Classifier.ClassifyAsync(context, ct);
    }

    /// <inheritdoc />
    public void ReportTurnOutcome(string conversationId, TurnOutcome outcome)
    {
        _escalationTracker.RecordOutcome(conversationId, outcome);
    }

    private ModelTier GetDefaultTier() =>
        _orderedTiers.FirstOrDefault(t => t.Name.Equals(_config.DefaultTier, StringComparison.OrdinalIgnoreCase))
        ?? _orderedTiers.First();

    private ModelTier GetBaseTierForComplexity(TaskComplexity complexity)
    {
        var index = complexity switch
        {
            TaskComplexity.Trivial => 0,
            TaskComplexity.Simple => 0,
            TaskComplexity.Moderate => Math.Min(1, _orderedTiers.Count - 1),
            TaskComplexity.Complex => Math.Min(2, _orderedTiers.Count - 1),
            _ => Math.Min(1, _orderedTiers.Count - 1)
        };
        return _orderedTiers[index];
    }

    private async Task<ModelRoutingDecision> BuildDecisionForTierAsync(
        ModelTier tier,
        TaskComplexity complexity,
        ClassificationSource source,
        double confidence,
        string? reasoning,
        bool wasEscalated,
        CancellationToken ct)
    {
        var client = await _clientFactory.GetChatClientAsync(tier.ClientType, tier.DeploymentName, ct);

        return new ModelRoutingDecision
        {
            SelectedTier = tier,
            Client = client,
            Complexity = complexity,
            Source = source,
            Confidence = confidence,
            Reasoning = reasoning,
            WasEscalated = wasEscalated
        };
    }
}

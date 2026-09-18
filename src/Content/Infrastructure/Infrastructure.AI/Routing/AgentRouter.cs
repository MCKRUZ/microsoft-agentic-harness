using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Routing;

/// <summary>
/// Front-door agent router: decides which registered agent should own a request that hasn't
/// named one. Classifies the request's intent first — a low-confidence classification means the
/// request is too ambiguous to route on, so this returns <see langword="null"/> without ever
/// trying to match an agent — then scores every registered agent's <c>AGENT.md</c> metadata via
/// the keyed <c>"agent-match"</c> <see cref="ISupervisorStrategy"/>.
/// </summary>
public sealed class AgentRouter : IAgentRouter
{
    /// <summary>
    /// Minimum intent-classification confidence required before attempting to route. Below this,
    /// the request is too ambiguous for a description-keyword match to be trustworthy, and a
    /// wrong guess here silently binds a conversation to the wrong agent for its whole lifetime —
    /// worse than falling back to the configured default.
    /// </summary>
    private const double MinIntentConfidence = 0.4;

    private readonly IRequestIntentClassifier _intentClassifier;
    private readonly IAgentMetadataRegistry _agentRegistry;
    private readonly ISupervisorStrategy _strategy;
    private readonly ILogger<AgentRouter> _logger;

    /// <summary>Initializes a new instance of <see cref="AgentRouter"/>.</summary>
    /// <param name="intentClassifier">Classifies the request before attempting to route it.</param>
    /// <param name="agentRegistry">Source of every registered agent to consider as a candidate.</param>
    /// <param name="strategy">The <c>"agent-match"</c> keyed scoring strategy.</param>
    /// <param name="logger">Logger for routing decisions and ambiguity refusals.</param>
    public AgentRouter(
        IRequestIntentClassifier intentClassifier,
        IAgentMetadataRegistry agentRegistry,
        [FromKeyedServices("agent-match")] ISupervisorStrategy strategy,
        ILogger<AgentRouter> logger)
    {
        _intentClassifier = intentClassifier;
        _agentRegistry = agentRegistry;
        _strategy = strategy;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<AgentSelection?> RouteAsync(string userMessage, CancellationToken ct = default)
    {
        var conversationId = Guid.NewGuid().ToString();
        var turnContext = new Domain.AI.Routing.Models.AgentTurnContext
        {
            ConversationId = conversationId,
            UserMessage = userMessage,
            TurnNumber = 1
        };

        var intent = await _intentClassifier.ClassifyAsync(turnContext, ct);
        if (intent.Confidence < MinIntentConfidence)
        {
            _logger.LogInformation(
                "Declined to route request {ConversationId}: intent classification confidence {Confidence:F2} below threshold {Threshold:F2}",
                conversationId, intent.Confidence, MinIntentConfidence);
            return null;
        }

        var agents = _agentRegistry.GetAll();
        if (agents.Count == 0)
            return null;

        var candidates = agents
            .Select(ToCandidate)
            .ToList();

        var decisionContext = new SupervisorDecisionContext
        {
            TaskDescription = userMessage,
            RequiredCapabilities = [],
            MinimumAutonomyLevel = AutonomyLevel.Restricted,
            AvailableAgents = candidates,
            CurrentDelegationDepth = 0,
            MaxDelegationDepth = 0
        };

        var selection = _strategy.SelectAgent(decisionContext);
        if (selection is null)
        {
            _logger.LogInformation(
                "Declined to route request {ConversationId}: no registered agent's description/tags matched (intent {Intent}, confidence {Confidence:F2})",
                conversationId, intent.Intent, intent.Confidence);
            return null;
        }

        return selection with
        {
            Reasoning = $"Intent: {intent.Intent} (confidence {intent.Confidence:F2}). {selection.Reasoning}"
        };
    }

    private static AgentCandidate ToCandidate(AgentDefinition agentDef) => new()
    {
        AgentId = agentDef.Id,
        AgentType = SubagentType.NamedAgent,
        AutonomyLevel = AutonomyLevel.Restricted,
        AvailableTools = [],
        Description = agentDef.Description,
        Tags = agentDef.Tags,
        Category = agentDef.Category,
        Domain = agentDef.Domain
    };
}

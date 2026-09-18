using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Infrastructure.AI.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Routing;

public class AgentRouterTests
{
    private readonly Mock<IRequestIntentClassifier> _classifier = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly Mock<ISupervisorStrategy> _strategy = new();
    private readonly AgentRouter _router;

    public AgentRouterTests()
    {
        _router = new AgentRouter(_classifier.Object, _agentRegistry.Object, _strategy.Object, NullLogger<AgentRouter>.Instance);
    }

    private static RequestIntentAssessment Intent(double confidence, RequestIntent intent = RequestIntent.Research) => new()
    {
        Intent = intent,
        Confidence = confidence,
        Source = ClassificationSource.LlmClassifier
    };

    private static AgentDefinition Agent(string id, string description = "") => new()
    {
        Id = id,
        Name = id,
        Description = description
    };

    private static AgentSelection Selection(string agentId) => new()
    {
        SelectedAgent = new AgentCandidate
        {
            AgentId = agentId,
            AgentType = SubagentType.NamedAgent,
            AutonomyLevel = AutonomyLevel.Restricted,
            AvailableTools = []
        },
        ConfidenceScore = 0.8,
        Reasoning = "matched on description"
    };

    [Fact]
    public async Task RouteAsync_LowIntentConfidence_ReturnsNullWithoutCallingStrategy()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent(0.1));

        var result = await _router.RouteAsync("hmm");

        Assert.Null(result);
        _strategy.Verify(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()), Times.Never);
    }

    [Fact]
    public async Task RouteAsync_NoRegisteredAgents_ReturnsNull()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent(0.9));
        _agentRegistry.Setup(r => r.GetAll()).Returns([]);

        var result = await _router.RouteAsync("find prior art");

        Assert.Null(result);
    }

    [Fact]
    public async Task RouteAsync_StrategyDeclines_ReturnsNull()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent(0.9));
        _agentRegistry.Setup(r => r.GetAll()).Returns([Agent("research-agent")]);
        _strategy.Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>())).Returns((AgentSelection?)null);

        var result = await _router.RouteAsync("find prior art");

        Assert.Null(result);
    }

    [Fact]
    public async Task RouteAsync_ConfidentIntentAndMatch_ReturnsSelectionWithComposedReasoning()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent(0.9, RequestIntent.Research));
        _agentRegistry.Setup(r => r.GetAll()).Returns([Agent("research-agent", "Investigates things")]);
        _strategy.Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>())).Returns(Selection("research-agent"));

        var result = await _router.RouteAsync("find prior art");

        Assert.NotNull(result);
        Assert.Equal("research-agent", result!.SelectedAgent.AgentId);
        Assert.Contains("Research", result.Reasoning);
        Assert.Contains("matched on description", result.Reasoning);
    }

    [Fact]
    public async Task RouteAsync_BuildsCandidatesFromRegistryMetadata()
    {
        _classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent(0.9));
        _agentRegistry.Setup(r => r.GetAll()).Returns([
            new AgentDefinition
            {
                Id = "research-agent",
                Name = "Research Agent",
                Description = "Investigates prior art",
                Category = "research",
                Domain = "analysis",
                Tags = ["research", "analysis"]
            }
        ]);

        SupervisorDecisionContext? captured = null;
        _strategy.Setup(s => s.SelectAgent(It.IsAny<SupervisorDecisionContext>()))
            .Callback<SupervisorDecisionContext>(c => captured = c)
            .Returns(Selection("research-agent"));

        await _router.RouteAsync("find prior art");

        Assert.NotNull(captured);
        var candidate = Assert.Single(captured!.AvailableAgents);
        Assert.Equal("research-agent", candidate.AgentId);
        Assert.Equal("Investigates prior art", candidate.Description);
        Assert.Equal("research", candidate.Category);
        Assert.Equal("analysis", candidate.Domain);
        Assert.Contains("research", candidate.Tags);
    }
}

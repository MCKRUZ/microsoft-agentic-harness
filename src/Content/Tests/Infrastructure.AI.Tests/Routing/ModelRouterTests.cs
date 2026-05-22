// src/Content/Tests/Infrastructure.AI.Tests/Routing/ModelRouterTests.cs
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Routing;
using Infrastructure.AI.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Routing;

public class ModelRouterTests
{
    private readonly Mock<ITaskComplexityHeuristic> _mockHeuristic = new();
    private readonly Mock<ITaskComplexityClassifier> _mockClassifier = new();
    private readonly Mock<IEscalationTracker> _mockEscalation = new();
    private readonly Mock<IChatClientFactory> _mockClientFactory = new();
    private readonly Mock<IChatClient> _mockClient = new();
    private readonly Mock<IServiceProvider> _mockServiceProvider = new();
    private readonly ModelRouter _sut;
    private readonly ModelRoutingConfig _config;

    public ModelRouterTests()
    {
        _config = new ModelRoutingConfig
        {
            Enabled = true,
            DefaultTier = "standard",
            HeuristicConfidenceThreshold = 0.8,
            Tiers =
            [
                new ModelRoutingTierConfig { Name = "economy", ClientType = AIAgentFrameworkClientType.OpenAI, DeploymentName = "gpt-4o-mini", EstimatedCostPer1KTokens = 0.00015m },
                new ModelRoutingTierConfig { Name = "standard", ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentName = "gpt-4o", EstimatedCostPer1KTokens = 0.005m },
                new ModelRoutingTierConfig { Name = "premium", ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentName = "o3", EstimatedCostPer1KTokens = 0.015m },
            ],
            OperationOverrides = new Dictionary<string, string>
            {
                ["raptor_summarization"] = "economy",
                ["crag_evaluation"] = "standard"
            }
        };

        _mockClientFactory
            .Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockClient.Object);

        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(ITaskComplexityClassifier)))
            .Returns(_mockClassifier.Object);

        _sut = new ModelRouter(
            _mockHeuristic.Object,
            _mockServiceProvider.Object,
            _mockEscalation.Object,
            _mockClientFactory.Object,
            Options.Create(_config),
            NullLogger<ModelRouter>.Instance);
    }

    [Fact]
    public async Task RouteAgentTurnAsync_HeuristicConfident_SkipsLlm()
    {
        var assessment = new TaskComplexityAssessment
        {
            Complexity = TaskComplexity.Trivial,
            Confidence = 0.95,
            Source = ClassificationSource.Heuristic
        };
        _mockHeuristic.Setup(h => h.Classify(It.IsAny<AgentTurnContext>())).Returns(assessment);

        var economyTier = new ModelTier
        {
            Name = "economy",
            ClientType = AIAgentFrameworkClientType.OpenAI,
            DeploymentName = "gpt-4o-mini",
            EstimatedCostPer1KTokens = 0.00015m
        };
        _mockEscalation
            .Setup(e => e.GetEffectiveTier(It.IsAny<string>(), TaskComplexity.Trivial, It.IsAny<IReadOnlyList<ModelTier>>()))
            .Returns(economyTier);

        var context = new AgentTurnContext { ConversationId = "test", UserMessage = "hi", TurnNumber = 1 };
        var result = await _sut.RouteAgentTurnAsync(context);

        Assert.Equal(TaskComplexity.Trivial, result.Complexity);
        Assert.Equal(ClassificationSource.Heuristic, result.Source);
        Assert.Equal("economy", result.SelectedTier.Name);
        _mockClassifier.Verify(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RouteAgentTurnAsync_HeuristicNull_FallsBackToLlm()
    {
        _mockHeuristic.Setup(h => h.Classify(It.IsAny<AgentTurnContext>())).Returns((TaskComplexityAssessment?)null);

        var llmAssessment = new TaskComplexityAssessment
        {
            Complexity = TaskComplexity.Moderate,
            Confidence = 0.85,
            Source = ClassificationSource.LlmClassifier
        };
        _mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmAssessment);

        var standardTier = new ModelTier
        {
            Name = "standard",
            ClientType = AIAgentFrameworkClientType.AzureOpenAI,
            DeploymentName = "gpt-4o",
            EstimatedCostPer1KTokens = 0.005m
        };
        _mockEscalation
            .Setup(e => e.GetEffectiveTier(It.IsAny<string>(), TaskComplexity.Moderate, It.IsAny<IReadOnlyList<ModelTier>>()))
            .Returns(standardTier);

        var context = new AgentTurnContext { ConversationId = "test", UserMessage = "analyze this", TurnNumber = 3, AvailableToolCount = 4 };
        var result = await _sut.RouteAgentTurnAsync(context);

        Assert.Equal(TaskComplexity.Moderate, result.Complexity);
        Assert.Equal(ClassificationSource.LlmClassifier, result.Source);
        Assert.Equal("standard", result.SelectedTier.Name);
        _mockClassifier.Verify(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RouteOperationAsync_KnownOperation_ReturnsConfiguredTier()
    {
        var result = await _sut.RouteOperationAsync("raptor_summarization");

        Assert.Equal("economy", result.SelectedTier.Name);
        _mockClientFactory.Verify(f => f.GetChatClientAsync(AIAgentFrameworkClientType.OpenAI, "gpt-4o-mini", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RouteOperationAsync_UnknownOperation_ReturnsDefaultTier()
    {
        var result = await _sut.RouteOperationAsync("unknown_operation");

        Assert.Equal("standard", result.SelectedTier.Name);
    }

    [Fact]
    public void ReportTurnOutcome_DelegatesToEscalationTracker()
    {
        _sut.ReportTurnOutcome("conv-1", TurnOutcome.UserCorrection);

        _mockEscalation.Verify(e => e.RecordOutcome("conv-1", TurnOutcome.UserCorrection), Times.Once);
    }

    [Fact]
    public async Task RouteAgentTurnAsync_RoutingDisabled_ReturnsDefaultTier()
    {
        var config = new ModelRoutingConfig
        {
            Enabled = false,
            DefaultTier = "standard",
            Tiers = _config.Tiers
        };

        var sut = new ModelRouter(
            _mockHeuristic.Object,
            _mockServiceProvider.Object,
            _mockEscalation.Object,
            _mockClientFactory.Object,
            Options.Create(config),
            NullLogger<ModelRouter>.Instance);

        var context = new AgentTurnContext { ConversationId = "test", UserMessage = "complex task", TurnNumber = 5, AvailableToolCount = 10 };
        var result = await sut.RouteAgentTurnAsync(context);

        Assert.Equal("standard", result.SelectedTier.Name);
        _mockHeuristic.Verify(h => h.Classify(It.IsAny<AgentTurnContext>()), Times.Never);
    }
}

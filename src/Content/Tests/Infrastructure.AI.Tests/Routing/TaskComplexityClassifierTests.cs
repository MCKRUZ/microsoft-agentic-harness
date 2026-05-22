// src/Content/Tests/Infrastructure.AI.Tests/Routing/TaskComplexityClassifierTests.cs
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Infrastructure.AI.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Routing;

public class TaskComplexityClassifierTests
{
    [Fact]
    public async Task ClassifyAsync_ValidResponse_ReturnsAssessment()
    {
        var mockRouter = new Mock<IModelRouter>();
        var mockClient = new Mock<IChatClient>();
        var routingDecision = MakeDecision(mockClient.Object);
        mockRouter
            .Setup(r => r.RouteOperationAsync("complexity_classification", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        var responseJson = """{"complexity": "moderate", "confidence": 0.85, "reasoning": "Multi-step analysis required"}""";
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson)));

        var classifier = new TaskComplexityClassifier(mockRouter.Object, NullLogger<TaskComplexityClassifier>.Instance);

        var context = new AgentTurnContext
        {
            ConversationId = "test-001",
            UserMessage = "Analyze the dependency graph and suggest improvements",
            TurnNumber = 3,
            AvailableToolCount = 5
        };

        var result = await classifier.ClassifyAsync(context);

        Assert.Equal(TaskComplexity.Moderate, result.Complexity);
        Assert.Equal(0.85, result.Confidence);
        Assert.Equal(ClassificationSource.LlmClassifier, result.Source);
        Assert.NotNull(result.Reasoning);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidResponse_FallsBackToModerate()
    {
        var mockRouter = new Mock<IModelRouter>();
        var mockClient = new Mock<IChatClient>();
        var routingDecision = MakeDecision(mockClient.Object);
        mockRouter
            .Setup(r => r.RouteOperationAsync("complexity_classification", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid json")));

        var classifier = new TaskComplexityClassifier(mockRouter.Object, NullLogger<TaskComplexityClassifier>.Instance);

        var context = new AgentTurnContext
        {
            ConversationId = "test-002",
            UserMessage = "Do something",
            TurnNumber = 1
        };

        var result = await classifier.ClassifyAsync(context);

        Assert.Equal(TaskComplexity.Moderate, result.Complexity);
        Assert.Equal(0.5, result.Confidence);
        Assert.Equal(ClassificationSource.LlmClassifier, result.Source);
    }

    [Fact]
    public async Task ClassifyAsync_ClientThrows_FallsBackToModerate()
    {
        var mockRouter = new Mock<IModelRouter>();
        var mockClient = new Mock<IChatClient>();
        var routingDecision = MakeDecision(mockClient.Object);
        mockRouter
            .Setup(r => r.RouteOperationAsync("complexity_classification", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var classifier = new TaskComplexityClassifier(mockRouter.Object, NullLogger<TaskComplexityClassifier>.Instance);

        var context = new AgentTurnContext
        {
            ConversationId = "test-003",
            UserMessage = "Do something",
            TurnNumber = 1
        };

        var result = await classifier.ClassifyAsync(context);

        Assert.Equal(TaskComplexity.Moderate, result.Complexity);
        Assert.Equal(0.5, result.Confidence);
    }

    private static ModelRoutingDecision MakeDecision(IChatClient client) => new()
    {
        SelectedTier = new ModelTier
        {
            Name = "economy",
            ClientType = Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI,
            DeploymentName = "gpt-4o-mini",
            EstimatedCostPer1KTokens = 0.00015m
        },
        Client = client,
        Complexity = TaskComplexity.Simple,
        Source = ClassificationSource.Heuristic,
        Confidence = 0.9
    };
}

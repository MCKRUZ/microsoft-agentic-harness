// src/Content/Tests/Infrastructure.AI.Tests/Routing/RequestIntentClassifierTests.cs
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Infrastructure.AI.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Routing;

public class RequestIntentClassifierTests
{
    [Fact]
    public async Task ClassifyAsync_ValidResponse_ReturnsAssessment()
    {
        var mockRouter = new Mock<IModelRouter>();
        var mockClient = new Mock<IChatClient>();
        var routingDecision = MakeDecision(mockClient.Object);
        mockRouter
            .Setup(r => r.RouteOperationAsync("intent_classification", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        var responseJson = """{"intent": "code_generation", "confidence": 0.9, "reasoning": "Asks to modify source code"}""";
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson)));

        var classifier = new RequestIntentClassifier(mockRouter.Object, NullLogger<RequestIntentClassifier>.Instance);

        var context = new AgentTurnContext
        {
            ConversationId = "test-001",
            UserMessage = "Add a null check to this method",
            TurnNumber = 1
        };

        var result = await classifier.ClassifyAsync(context);

        Assert.Equal(RequestIntent.CodeGeneration, result.Intent);
        Assert.Equal(0.9, result.Confidence);
        Assert.Equal(ClassificationSource.LlmClassifier, result.Source);
        Assert.NotNull(result.Reasoning);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidResponse_FallsBackToOther()
    {
        var mockRouter = new Mock<IModelRouter>();
        var mockClient = new Mock<IChatClient>();
        var routingDecision = MakeDecision(mockClient.Object);
        mockRouter
            .Setup(r => r.RouteOperationAsync("intent_classification", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid json")));

        var classifier = new RequestIntentClassifier(mockRouter.Object, NullLogger<RequestIntentClassifier>.Instance);

        var context = new AgentTurnContext
        {
            ConversationId = "test-002",
            UserMessage = "Do something",
            TurnNumber = 1
        };

        var result = await classifier.ClassifyAsync(context);

        Assert.Equal(RequestIntent.Other, result.Intent);
        Assert.Equal(0.5, result.Confidence);
        Assert.Equal(ClassificationSource.LlmClassifier, result.Source);
    }

    [Fact]
    public async Task ClassifyAsync_ClientThrows_FallsBackToOther()
    {
        var mockRouter = new Mock<IModelRouter>();
        var mockClient = new Mock<IChatClient>();
        var routingDecision = MakeDecision(mockClient.Object);
        mockRouter
            .Setup(r => r.RouteOperationAsync("intent_classification", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var classifier = new RequestIntentClassifier(mockRouter.Object, NullLogger<RequestIntentClassifier>.Instance);

        var context = new AgentTurnContext
        {
            ConversationId = "test-003",
            UserMessage = "Do something",
            TurnNumber = 1
        };

        var result = await classifier.ClassifyAsync(context);

        Assert.Equal(RequestIntent.Other, result.Intent);
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

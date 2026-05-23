using Application.AI.Common.Interfaces.Routing;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Routing.Models;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FluentAssertions;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

public class ConversationFactExtractorTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockClient = new();
    private readonly ConversationFactExtractor _sut;

    public ConversationFactExtractorTests()
    {
        var routingDecision = new ModelRoutingDecision
        {
            SelectedTier = new ModelTier
            {
                Name = "economy",
                ClientType = Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI,
                DeploymentName = "gpt-4o-mini",
                EstimatedCostPer1KTokens = 0.00015m
            },
            Client = _mockClient.Object,
            Complexity = Domain.AI.Routing.Enums.TaskComplexity.Trivial,
            Source = Domain.AI.Routing.Enums.ClassificationSource.Heuristic,
            Confidence = 1.0
        };

        _mockRouter
            .Setup(r => r.RouteOperationAsync("fact_extraction", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        _sut = new ConversationFactExtractor(
            _mockRouter.Object,
            NullLogger<ConversationFactExtractor>.Instance);
    }

    [Fact]
    public async Task ExtractAsync_ValidJson_ReturnsFactsWithDeterministicKeys()
    {
        SetupLlmResponse("""
            [
              {"key": "user_prefers_postgresql", "content": "User prefers PostgreSQL", "entity_type": "Preference", "confidence": 0.92},
              {"key": "deadline_june", "content": "Deployment deadline is June 15", "entity_type": "Decision", "confidence": 0.88}
            ]
            """);

        var result = await _sut.ExtractAsync("I prefer PostgreSQL", "Noted, I'll use PostgreSQL", "conv-1", 3);

        result.Should().HaveCount(2);
        result[0].Key.Should().Be("conv-1:3:0");
        result[0].Content.Should().Be("User prefers PostgreSQL");
        result[0].EntityType.Should().Be("Preference");
        result[0].Confidence.Should().Be(0.92);
        result[1].Key.Should().Be("conv-1:3:1");
    }

    [Fact]
    public async Task ExtractAsync_EmptyArray_ReturnsEmptyList()
    {
        SetupLlmResponse("[]");
        var result = await _sut.ExtractAsync("run the tests", "Tests passed.", "conv-1", 1);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmptyList()
    {
        SetupLlmResponse("this is not json at all");
        var result = await _sut.ExtractAsync("hello", "hi there", "conv-1", 1);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_AllBelowConfidence_ReturnsEmptyList()
    {
        SetupLlmResponse("""
            [
              {"key": "low_conf", "content": "Might prefer dark mode", "entity_type": "Preference", "confidence": 0.3}
            ]
            """);
        var result = await _sut.ExtractAsync("maybe dark mode?", "I can set that up", "conv-1", 1);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MixedConfidence_ReturnsOnlyAboveThreshold()
    {
        SetupLlmResponse("""
            [
              {"key": "high", "content": "PostgreSQL preferred", "entity_type": "Preference", "confidence": 0.9},
              {"key": "low", "content": "Maybe uses VS Code", "entity_type": "Fact", "confidence": 0.4},
              {"key": "medium", "content": "Project deadline June", "entity_type": "Decision", "confidence": 0.75}
            ]
            """);

        var result = await _sut.ExtractAsync("user msg", "assistant msg", "conv-1", 2);

        result.Should().HaveCount(2);
        result[0].Key.Should().Be("conv-1:2:0");
        result[0].Confidence.Should().Be(0.9);
        result[1].Key.Should().Be("conv-1:2:1");
        result[1].Confidence.Should().Be(0.75);
    }

    [Fact]
    public async Task ExtractAsync_LlmThrows_ReturnsEmptyList()
    {
        _mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var result = await _sut.ExtractAsync("msg", "resp", "conv-1", 1);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_JsonWrappedInMarkdown_ExtractsCorrectly()
    {
        SetupLlmResponse("""
            ```json
            [
              {"key": "fact1", "content": "API rate limit is 1000/min", "entity_type": "Fact", "confidence": 0.85}
            ]
            ```
            """);

        var result = await _sut.ExtractAsync("what's the rate limit?", "The API rate limit is 1000 requests per minute.", "conv-1", 5);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("API rate limit is 1000/min");
    }

    [Fact]
    public async Task ExtractAsync_DefaultConfidenceThreshold_Is07()
    {
        SetupLlmResponse("""
            [
              {"key": "exactly_at", "content": "Exactly at threshold", "entity_type": "Fact", "confidence": 0.7},
              {"key": "just_below", "content": "Just below threshold", "entity_type": "Fact", "confidence": 0.69}
            ]
            """);

        var result = await _sut.ExtractAsync("msg", "resp", "conv-1", 1);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("Exactly at threshold");
    }

    private void SetupLlmResponse(string responseText)
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
        _mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);
    }
}

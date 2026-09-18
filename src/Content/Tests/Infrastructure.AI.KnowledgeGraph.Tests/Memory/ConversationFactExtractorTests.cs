using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Prompts;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FluentAssertions;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

public class ConversationFactExtractorTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockClient = new();
    private readonly Mock<IPromptRegistry> _mockRegistry = new();
    private readonly Mock<IPromptRenderer> _mockRenderer = new();
    private readonly Mock<IPromptUsageRecorder> _mockRecorder = new();
    private readonly Mock<IOptions<KnowledgeBridgeConfig>> _mockConfig = new();
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

        var descriptor = new PromptDescriptor
        {
            Name = "conversation-fact-extractor",
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "Extract facts from <user_message>{{user_message}}</user_message><assistant_message>{{assistant_message}}</assistant_message>",
        };

        _mockRegistry
            .Setup(r => r.GetLatestAsync("conversation-fact-extractor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(descriptor);

        _mockRenderer
            .Setup(r => r.RenderAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, IReadOnlyDictionary<string, object?> _, CancellationToken __)
                => new RenderedPrompt { Source = d, Body = "rendered-prompt-body" });

        _mockRecorder
            .Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, PromptUsageContext c, CancellationToken _) => new PromptUsageRecord
            {
                Descriptor = d,
                CaseId = c.CaseId,
                MetricKey = c.MetricKey,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

        _mockConfig.Setup(c => c.Value).Returns(new KnowledgeBridgeConfig { MinConfidence = 0.7 });

        _sut = new ConversationFactExtractor(
            _mockRouter.Object,
            _mockRegistry.Object,
            _mockRenderer.Object,
            _mockRecorder.Object,
            _mockConfig.Object,
            NullLogger<ConversationFactExtractor>.Instance);
    }

    [Fact]
    public async Task ExtractAsync_PromptUnavailable_ReturnsEmptyAndSkipsLlm()
    {
        _mockRegistry
            .Setup(r => r.GetLatestAsync("conversation-fact-extractor", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PromptRegistryUnavailableException(
                "conversation-fact-extractor",
                "backend offline",
                new IOException("blip")));

        var result = await _sut.ExtractAsync("u", "a", "conv-1", 1);

        result.Should().BeEmpty();
        _mockClient.VerifyNoOtherCalls();
        _mockRecorder.Verify(
            r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractAsync_Successful_RecordsPromptUsageWithConversationAndTurn()
    {
        SetupLlmResponse("[]");

        await _sut.ExtractAsync("u", "a", "conv-42", 7);

        _mockRecorder.Verify(
            r => r.RecordAsync(
                It.Is<PromptDescriptor>(d => d.Name == "conversation-fact-extractor"),
                It.Is<PromptUsageContext>(c => c.CaseId == "conv-42:7" && c.MetricKey == "fact_extraction"),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task ExtractAsync_ConfigThresholdRaised_DiscardsFactsThatPassedTheDefault()
    {
        // Proves MinConfidence is genuinely read from the injected config, not a fixed const baked into
        // the extractor — deleting the config wiring and reverting to a hardcoded 0.7 would make this
        // test fail even though ExtractAsync_DefaultConfidenceThreshold_Is07 above still passes.
        _mockConfig.Setup(c => c.Value).Returns(new KnowledgeBridgeConfig { MinConfidence = 0.95 });
        SetupLlmResponse("""
            [
              {"key": "used_to_pass", "content": "Confident but not confident enough now", "entity_type": "Fact", "confidence": 0.9}
            ]
            """);

        var result = await _sut.ExtractAsync("msg", "resp", "conv-1", 1);

        result.Should().BeEmpty("0.9 cleared the old 0.7 default but not the raised 0.95 threshold");
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

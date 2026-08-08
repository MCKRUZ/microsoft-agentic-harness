using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Domain.AI.RAG.Enums;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.QueryTransform;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.QueryTransform;

/// <summary>
/// Coverage for the prompt-registry migration of LlmQueryClassifier: prompt resolution,
/// rendering, usage recording, and fallback semantics on resolution / classification failure.
/// </summary>
public sealed class LlmQueryClassifierTests
{
    private readonly Mock<IModelRouter> _router = new();
    private readonly Mock<IChatClient> _chat = new();
    private readonly Mock<IPromptRegistry> _registry = new();
    private readonly Mock<IPromptRenderer> _renderer = new();
    private readonly Mock<IPromptUsageRecorder> _recorder = new();
    private readonly LlmQueryClassifier _sut;

    public LlmQueryClassifierTests()
    {
        _router
            .Setup(r => r.RouteOperationAsync("query_classification", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelRoutingDecision
            {
                SelectedTier = new ModelTier
                {
                    Name = "economy",
                    ClientType = Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI,
                    DeploymentName = "gpt-4o-mini",
                    EstimatedCostPer1KTokens = 0.00015m
                },
                Client = _chat.Object,
                Complexity = TaskComplexity.Trivial,
                Source = ClassificationSource.Heuristic,
                Confidence = 1.0
            });

        var descriptor = new PromptDescriptor
        {
            Name = "query-classifier",
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "Classify: {{ query }}",
        };
        _registry
            .Setup(r => r.GetLatestAsync("query-classifier", It.IsAny<CancellationToken>()))
            .ReturnsAsync(descriptor);
        _renderer
            .Setup(r => r.RenderAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, IReadOnlyDictionary<string, object?> _, CancellationToken __)
                => new RenderedPrompt { Source = d, Body = "rendered" });
        _recorder
            .Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, PromptUsageContext c, CancellationToken _) => new PromptUsageRecord
            {
                Descriptor = d,
                MetricKey = c.MetricKey,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

        _sut = new LlmQueryClassifier(
            _router.Object,
            _registry.Object,
            _renderer.Object,
            _recorder.Object,
            NullLogger<LlmQueryClassifier>.Instance);
    }

    [Fact]
    public async Task ClassifyAsync_PromptUnavailable_ReturnsFallback()
    {
        _registry
            .Setup(r => r.GetLatestAsync("query-classifier", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PromptRegistryUnavailableException(
                "query-classifier",
                "backend offline",
                new IOException("blip")));

        var result = await _sut.ClassifyAsync("anything");

        result.Type.Should().Be(QueryType.SimpleLookup);
        result.Strategy.Should().Be(RetrievalStrategy.HybridVectorBm25);
        result.Reasoning.Should().Contain("Prompt unavailable");
        _chat.VerifyNoOtherCalls();
        _recorder.Verify(
            r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ClassifyAsync_ValidJson_ReturnsParsedClassificationAndRecordsUsage()
    {
        SetupChatResponse("""{"type": "MultiHop", "strategy": "MultiQueryFusion", "confidence": 0.85, "reasoning": "joins two facts"}""");

        var result = await _sut.ClassifyAsync("What was the revenue in 2022 and how did it affect 2023 margins?");

        result.Type.Should().Be(QueryType.MultiHop);
        result.Strategy.Should().Be(RetrievalStrategy.MultiQueryFusion);
        result.Confidence.Should().Be(0.85);
        _recorder.Verify(
            r => r.RecordAsync(
                It.Is<PromptDescriptor>(d => d.Name == "query-classifier"),
                It.Is<PromptUsageContext>(c => c.MetricKey == "query_classification"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ClassifyAsync_LlmThrows_ReturnsFallback()
    {
        _chat
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var result = await _sut.ClassifyAsync("any");

        result.Type.Should().Be(QueryType.SimpleLookup);
        result.Strategy.Should().Be(RetrievalStrategy.HybridVectorBm25);
        result.Reasoning.Should().Contain("Classification failed");
    }

    [Fact]
    public async Task ClassifyAsync_MalformedJson_FallsBackToSimpleLookup()
    {
        SetupChatResponse("this is not json");

        var result = await _sut.ClassifyAsync("any");

        result.Type.Should().Be(QueryType.SimpleLookup);
        result.Reasoning.Should().Contain("JSON parse failure");
    }

    [Theory]
    [InlineData("99")]                      // outside the defined range
    [InlineData(" 99")]                     // and behind a stray space
    [InlineData("1")]                       // the numeric form of a real member
    [InlineData("SimpleLookup,MultiHop")]   // comma-composite
    public async Task ClassifyAsync_NonNameQueryType_FallsBackToSimpleLookup(string type)
    {
        // #300. The type is whatever the model emitted, so it is untrusted by definition. A bare
        // Enum.TryParse accepts all of these and produces a QueryType that then misses every entry
        // in DefaultStrategyMap — so the retrieval strategy is chosen by the map's fallback rather
        // than by the logged SimpleLookup default, and the log line saying so never prints.
        SetupChatResponse($$"""{"type": "{{type}}", "confidence": 0.9, "reasoning": "r"}""");

        var result = await _sut.ClassifyAsync("any");

        result.Type.Should().Be(QueryType.SimpleLookup);
        result.Strategy.Should().Be(RetrievalStrategy.HybridVectorBm25);
    }

    private void SetupChatResponse(string body)
    {
        _chat
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, body)));
    }
}

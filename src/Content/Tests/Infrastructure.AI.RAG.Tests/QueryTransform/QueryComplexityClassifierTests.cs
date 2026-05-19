using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using FluentAssertions;
using Infrastructure.AI.RAG.QueryTransform;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.QueryTransform;

public sealed class QueryComplexityClassifierTests
{
    private readonly Mock<IRagModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockChatClient = new();

    public QueryComplexityClassifierTests()
    {
        _mockRouter
            .Setup(r => r.GetClientForOperation("complexity_classification"))
            .Returns(_mockChatClient.Object);
    }

    private QueryComplexityClassifier CreateClassifier()
        => new(
            _mockRouter.Object,
            Mock.Of<ILogger<QueryComplexityClassifier>>());

    private void SetupChatResponse(string jsonResponse)
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, jsonResponse)));
    }

    [Fact]
    public async Task ClassifyAsync_TrivialQuery_ReturnsTrivial()
    {
        SetupChatResponse("""{"complexity":"trivial","confidence":0.95,"reasoning":"General knowledge question"}""");
        var classifier = CreateClassifier();

        var result = await classifier.ClassifyAsync("What is the capital of France?");

        result.Complexity.Should().Be(QueryComplexity.Trivial);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.9);
        result.SkipRetrieval.Should().BeTrue();
    }

    [Fact]
    public async Task ClassifyAsync_SimpleQuery_ReturnsSimple()
    {
        SetupChatResponse("""{"complexity":"simple","confidence":0.85,"reasoning":"Direct factual lookup"}""");
        var classifier = CreateClassifier();

        var result = await classifier.ClassifyAsync("What chunking strategies are available?");

        result.Complexity.Should().Be(QueryComplexity.Simple);
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.8);
        result.SkipRetrieval.Should().BeFalse();
    }

    [Fact]
    public async Task ClassifyAsync_ModerateQuery_ReturnsModerate()
    {
        SetupChatResponse("""{"complexity":"moderate","confidence":0.8,"reasoning":"Requires cross-referencing"}""");
        var classifier = CreateClassifier();

        var result = await classifier.ClassifyAsync("Compare the CRAG and Self-RAG approaches in our pipeline");

        result.Complexity.Should().Be(QueryComplexity.Moderate);
    }

    [Fact]
    public async Task ClassifyAsync_ComplexQuery_ReturnsComplex()
    {
        SetupChatResponse("""{"complexity":"complex","confidence":0.75,"reasoning":"Multi-hop reasoning needed"}""");
        var classifier = CreateClassifier();

        var result = await classifier.ClassifyAsync(
            "Based on the architecture docs and the deployment guide, what changes are needed to support multi-tenant GraphRAG?");

        result.Complexity.Should().Be(QueryComplexity.Complex);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidJson_FallsBackToModerate()
    {
        SetupChatResponse("I can't classify this properly");
        var classifier = CreateClassifier();

        var result = await classifier.ClassifyAsync("Some query");

        result.Complexity.Should().Be(QueryComplexity.Moderate);
        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task ClassifyAsync_LlmThrows_FallsBackToModerate()
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var classifier = CreateClassifier();

        var result = await classifier.ClassifyAsync("Some query");

        result.Complexity.Should().Be(QueryComplexity.Moderate);
        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task ClassifyAsync_ConfidenceClamped_StaysInRange()
    {
        SetupChatResponse("""{"complexity":"simple","confidence":1.5,"reasoning":"Over-confident"}""");
        var classifier = CreateClassifier();

        var result = await classifier.ClassifyAsync("Test query");

        result.Confidence.Should().BeInRange(0.0, 1.0);
    }
}

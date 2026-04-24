using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using FluentAssertions;
using Infrastructure.AI.RAG.Evaluation;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Evaluation;

public sealed class CragEvaluatorTests
{
    private readonly Mock<IRagModelRouter> _mockRouter = new();

    private CragEvaluator CreateEvaluator(
        double acceptThreshold = 0.7,
        double refineThreshold = 0.4,
        bool allowWebFallback = false)
    {
        var config = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.Crag.AcceptThreshold = acceptThreshold;
            c.AI.Rag.Crag.RefineThreshold = refineThreshold;
            c.AI.Rag.Crag.AllowWebFallback = allowWebFallback;
        });

        _mockRouter
            .Setup(r => r.GetTierForOperation(It.IsAny<string>()))
            .Returns("standard");

        return new CragEvaluator(
            _mockRouter.Object,
            config,
            Mock.Of<ILogger<CragEvaluator>>());
    }

    private void SetupChatResponse(string jsonResponse)
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, jsonResponse)));

        _mockRouter
            .Setup(r => r.GetClientForOperation(It.IsAny<string>()))
            .Returns(mockChatClient.Object);
    }

    [Fact]
    public async Task EvaluateAsync_HighRelevance_ReturnsAccept()
    {
        SetupChatResponse("""{"action":"Accept","score":0.85,"reasoning":"highly relevant","weak_chunk_ids":[]}""");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var evaluation = await evaluator.EvaluateAsync("test query", results);

        evaluation.Action.Should().Be(CorrectionAction.Accept);
        evaluation.RelevanceScore.Should().BeGreaterThanOrEqualTo(0.7);
    }

    [Fact]
    public async Task EvaluateAsync_MediumRelevance_ReturnsRefine()
    {
        SetupChatResponse("""{"action":"Refine","score":0.55,"reasoning":"partially relevant","weak_chunk_ids":["chunk-3"]}""");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var evaluation = await evaluator.EvaluateAsync("test query", results);

        evaluation.Action.Should().Be(CorrectionAction.Refine);
        evaluation.RelevanceScore.Should().BeInRange(0.4, 0.7);
    }

    [Fact]
    public async Task EvaluateAsync_LowRelevance_ReturnsReject()
    {
        SetupChatResponse("""{"action":"Reject","score":0.15,"reasoning":"not relevant","weak_chunk_ids":["chunk-1","chunk-2"]}""");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var evaluation = await evaluator.EvaluateAsync("test query", results);

        evaluation.Action.Should().Be(CorrectionAction.Reject);
        evaluation.RelevanceScore.Should().BeLessThan(0.4);
    }

    [Fact]
    public async Task EvaluateAsync_LlmFailure_ReturnsFallbackAccept()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        _mockRouter
            .Setup(r => r.GetClientForOperation(It.IsAny<string>()))
            .Returns(mockChatClient.Object);

        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var evaluation = await evaluator.EvaluateAsync("test query", results);

        evaluation.Action.Should().Be(CorrectionAction.Accept);
        evaluation.RelevanceScore.Should().Be(0.5);
        evaluation.Reasoning.Should().Contain("failed");
    }

    [Fact]
    public async Task EvaluateAsync_LowScoreWithWebFallback_ReturnsWebFallback()
    {
        SetupChatResponse("""{"action":"Reject","score":0.1,"reasoning":"irrelevant","weak_chunk_ids":[]}""");
        var evaluator = CreateEvaluator(allowWebFallback: true);
        var results = RagTestData.CreateRetrievalResults(2);

        var evaluation = await evaluator.EvaluateAsync("test query", results);

        evaluation.Action.Should().Be(CorrectionAction.WebFallback);
    }
}

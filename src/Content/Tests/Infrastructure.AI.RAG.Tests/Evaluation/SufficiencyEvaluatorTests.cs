using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Evaluation;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Evaluation;

public sealed class SufficiencyEvaluatorTests
{
    private readonly Mock<IRagModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockChatClient = new();

    public SufficiencyEvaluatorTests()
    {
        _mockRouter
            .Setup(r => r.GetClientForOperation("sufficiency_evaluation"))
            .Returns(_mockChatClient.Object);
    }

    private SufficiencyEvaluator CreateEvaluator()
        => new(
            _mockRouter.Object,
            Mock.Of<ILogger<SufficiencyEvaluator>>());

    private void SetupChatResponse(string response)
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, response)));
    }

    [Fact]
    public async Task EvaluateAsync_SufficientContext_ReturnsHighScore()
    {
        SetupChatResponse("""{"score": 0.92, "reasoning": "Context fully addresses the question"}""");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var score = await evaluator.EvaluateAsync("What is the default topK?", results);

        score.Should().BeGreaterThanOrEqualTo(0.9);
    }

    [Fact]
    public async Task EvaluateAsync_InsufficientContext_ReturnsLowScore()
    {
        SetupChatResponse("""{"score": 0.25, "reasoning": "Context does not address the question"}""");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(2);

        var score = await evaluator.EvaluateAsync("Unrelated sub-query", results);

        score.Should().BeLessThan(0.5);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyResults_ReturnsZero()
    {
        var evaluator = CreateEvaluator();
        IReadOnlyList<RetrievalResult> emptyResults = [];

        var score = await evaluator.EvaluateAsync("Any sub-query", emptyResults);

        score.Should().Be(0.0);
        _mockChatClient.Verify(
            c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_LlmReturnsInvalidResponse_ReturnsDefaultScore()
    {
        SetupChatResponse("I'm not sure how to evaluate this.");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var score = await evaluator.EvaluateAsync("Some sub-query", results);

        score.Should().Be(0.5);
    }

    [Fact]
    public async Task EvaluateAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var act = () => evaluator.EvaluateAsync("test", results, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

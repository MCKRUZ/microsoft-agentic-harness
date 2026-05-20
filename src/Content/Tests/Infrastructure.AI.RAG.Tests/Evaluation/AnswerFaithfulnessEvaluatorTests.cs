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

public sealed class AnswerFaithfulnessEvaluatorTests
{
    private readonly Mock<IRagModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockChatClient = new();

    public AnswerFaithfulnessEvaluatorTests()
    {
        _mockRouter
            .Setup(r => r.GetClientForOperation("faithfulness_evaluation"))
            .Returns(_mockChatClient.Object);
    }

    private AnswerFaithfulnessEvaluator CreateEvaluator()
        => new(
            _mockRouter.Object,
            Mock.Of<ILogger<AnswerFaithfulnessEvaluator>>());

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
    public async Task EvaluateAsync_FaithfulAnswer_ReturnsHighScore()
    {
        SetupChatResponse("""
            {
                "is_faithful": true,
                "score": 0.95,
                "supported_claims": ["The default topK is 10", "CRAG evaluation runs after reranking"],
                "hallucinated_claims": [],
                "reasoning": "All claims are directly supported by the retrieved context."
            }
            """);
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var result = await evaluator.EvaluateAsync("The default topK is 10 and CRAG runs after reranking.", context);

        result.IsFaithful.Should().BeTrue();
        result.Score.Should().BeGreaterThanOrEqualTo(0.9);
        result.SupportedClaims.Should().HaveCount(2);
        result.HallucinatedClaims.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_HallucinatedAnswer_ReturnsFlaggedClaims()
    {
        SetupChatResponse("""
            {
                "is_faithful": false,
                "score": 0.2,
                "supported_claims": ["The system uses hybrid retrieval"],
                "hallucinated_claims": ["The system uses GPT-5 for classification", "FAISS supports 10 billion vectors natively"],
                "reasoning": "Two claims have no support in the context."
            }
            """);
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var result = await evaluator.EvaluateAsync("Hallucinated answer text", context);

        result.IsFaithful.Should().BeFalse();
        result.Score.Should().BeLessThan(0.5);
        result.HallucinatedClaims.Should().HaveCount(2);
        result.HallucinatedClaims.Should().Contain(c => c.Contains("GPT-5"));
    }

    [Fact]
    public async Task EvaluateAsync_PartiallyFaithful_ReturnsMiddleScore()
    {
        SetupChatResponse("""
            {
                "is_faithful": false,
                "score": 0.55,
                "supported_claims": ["The pipeline has 5 stages", "Reranking improves accuracy"],
                "hallucinated_claims": ["The pipeline uses quantum computing"],
                "reasoning": "Most claims are supported but one is fabricated."
            }
            """);
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var result = await evaluator.EvaluateAsync("Partially faithful answer", context);

        result.Score.Should().BeInRange(0.4, 0.7);
        result.SupportedClaims.Should().NotBeEmpty();
        result.HallucinatedClaims.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_EmptyContext_ReturnsUnfaithful()
    {
        var evaluator = CreateEvaluator();
        IReadOnlyList<RerankedResult> emptyContext = [];

        var result = await evaluator.EvaluateAsync("Any answer", emptyContext);

        result.IsFaithful.Should().BeFalse();
        result.Score.Should().Be(0.0);
        _mockChatClient.Verify(
            c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_LlmReturnsInvalidJson_ReturnsFallbackEvaluation()
    {
        SetupChatResponse("I cannot evaluate this answer.");
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var result = await evaluator.EvaluateAsync("Some answer", context);

        result.IsFaithful.Should().BeFalse();
        result.Score.Should().Be(0.0);
        result.Reasoning.Should().Contain("failed");
    }

    [Fact]
    public async Task EvaluateAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var act = () => evaluator.EvaluateAsync("test", context, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

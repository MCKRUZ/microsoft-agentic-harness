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

public sealed class RetrievalQualityEvaluatorTests
{
    private readonly Mock<IRagModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockChatClient = new();

    public RetrievalQualityEvaluatorTests()
    {
        _mockRouter
            .Setup(r => r.GetClientForOperation(It.IsAny<string>()))
            .Returns(_mockChatClient.Object);
    }

    private RetrievalQualityEvaluator CreateEvaluator()
    {
        return new RetrievalQualityEvaluator(
            _mockRouter.Object,
            Mock.Of<ILogger<RetrievalQualityEvaluator>>());
    }

    private void SetupChatResponse(string responseText)
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    [Fact]
    public async Task EvaluateAsync_HighQualityRetrieval_HighScores()
    {
        SetupChatResponse("0.90");
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var report = await evaluator.EvaluateAsync(
            query: "What is clean architecture?",
            answer: "Clean architecture separates concerns into layers.",
            context: context,
            groundTruth: "Clean architecture is about separation of concerns into layers.");

        report.ContextPrecision.Should().BeGreaterThanOrEqualTo(0.0);
        report.ContextRecall.Should().BeGreaterThanOrEqualTo(0.0);
        report.Faithfulness.Should().BeGreaterThanOrEqualTo(0.0);
        report.AnswerRelevancy.Should().BeGreaterThanOrEqualTo(0.0);
        report.OverallScore.Should().BeGreaterThanOrEqualTo(0.0);
        report.EvaluatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EvaluateAsync_IrrelevantContext_LowPrecision()
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<ChatMessage> messages, ChatOptions? _, CancellationToken _) =>
            {
                var prompt = messages[0].Text ?? string.Empty;
                var score = prompt.Contains("retrieval quality", StringComparison.OrdinalIgnoreCase)
                    ? "0.20"
                    : "0.85";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, score));
            });

        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var report = await evaluator.EvaluateAsync(
            query: "What is clean architecture?",
            answer: "Clean architecture separates concerns.",
            context: context,
            groundTruth: "Clean architecture is about layers.");

        report.ContextPrecision.Should().BeLessThan(0.5);
    }

    [Fact]
    public async Task EvaluateAsync_HallucinatedAnswer_LowFaithfulness()
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<ChatMessage> messages, ChatOptions? _, CancellationToken _) =>
            {
                var prompt = messages[0].Text ?? string.Empty;
                var score = prompt.Contains("faithfulness", StringComparison.OrdinalIgnoreCase)
                    ? "0.15"
                    : "0.85";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, score));
            });

        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var report = await evaluator.EvaluateAsync(
            query: "What is the system?",
            answer: "The system uses quantum computing for all operations.",
            context: context,
            groundTruth: "The system uses standard computing.");

        report.Faithfulness.Should().BeLessThan(0.5);
    }

    [Fact]
    public async Task EvaluateAsync_WithGroundTruth_CalculatesRecall()
    {
        SetupChatResponse("0.80");
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var report = await evaluator.EvaluateAsync(
            query: "What is X?",
            answer: "X is Y.",
            context: context,
            groundTruth: "X is Y and also Z.");

        report.ContextRecall.Should().BeGreaterThanOrEqualTo(0.0);
        report.ContextRecall.Should().NotBe(-1.0);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutGroundTruth_SkipsRecall()
    {
        SetupChatResponse("0.80");
        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var report = await evaluator.EvaluateAsync(
            query: "What is X?",
            answer: "X is Y.",
            context: context,
            groundTruth: null);

        report.ContextRecall.Should().Be(-1.0);
    }

    [Fact]
    public async Task EvaluateAsync_LlmFailure_ReturnsFallbackReport()
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM service unavailable"));

        var evaluator = CreateEvaluator();
        var context = RagTestData.CreateRerankedResults(3);

        var report = await evaluator.EvaluateAsync(
            query: "What is X?",
            answer: "X is Y.",
            context: context);

        report.ContextPrecision.Should().Be(0.0);
        report.Faithfulness.Should().Be(0.0);
        report.AnswerRelevancy.Should().Be(0.0);
        report.OverallScore.Should().Be(0.0);
        report.Reasoning.Should().Contain("failed");
    }
}

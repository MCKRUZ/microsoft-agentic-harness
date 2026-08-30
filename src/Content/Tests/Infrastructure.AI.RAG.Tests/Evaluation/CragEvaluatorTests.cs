using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.StructuredOutput;
using Domain.AI.RAG.Enums;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI;
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
    private readonly Mock<IModelRouter> _mockRouter = new();

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

        // Real invoker, not a mock — proves the structured-output wiring works end to end.
        var structuredOutput = new StructuredOutputInvoker(Mock.Of<ILogger<StructuredOutputInvoker>>());

        return new CragEvaluator(
            _mockRouter.Object,
            structuredOutput,
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
            .Setup(r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelRoutingDecision
            {
                Client = mockChatClient.Object,
                SelectedTier = new ModelTier { Name = "standard", ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentName = "gpt-4o" },
                Complexity = TaskComplexity.Moderate,
                Source = ClassificationSource.Heuristic,
                Confidence = 1.0,
            });
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
    public async Task EvaluateAsync_LlmFailure_ReturnsEvaluationUnavailable_NeverAccept()
    {
        // THE CONTROL for #323's sharpest finding: a CRAG evaluator that cannot read its own
        // response must never silently green-light the retrieval it exists to gate. This test used
        // to assert the opposite (CorrectionAction.Accept) — that was the bug.
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        _mockRouter
            .Setup(r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelRoutingDecision
            {
                Client = mockChatClient.Object,
                SelectedTier = new ModelTier { Name = "standard", ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentName = "gpt-4o" },
                Complexity = TaskComplexity.Moderate,
                Source = ClassificationSource.Heuristic,
                Confidence = 1.0,
            });

        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var evaluation = await evaluator.EvaluateAsync("test query", results);

        evaluation.Action.Should().Be(CorrectionAction.EvaluationUnavailable);
        evaluation.Action.Should().NotBe(CorrectionAction.Accept);
        evaluation.RelevanceScore.Should().Be(0.5);
        evaluation.Reasoning.Should().Contain("failed");
    }

    [Fact]
    public async Task EvaluateAsync_MalformedResponseAfterRepairAttempt_ReturnsEvaluationUnavailable_NeverAccept()
    {
        // Second half of the same control: a response that never parses, even after the one
        // repair round-trip, must degrade the same way as a thrown exception — never a passing score.
        SetupChatResponse("this is not json at all, no braces here");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        var evaluation = await evaluator.EvaluateAsync("test query", results);

        evaluation.Action.Should().Be(CorrectionAction.EvaluationUnavailable);
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

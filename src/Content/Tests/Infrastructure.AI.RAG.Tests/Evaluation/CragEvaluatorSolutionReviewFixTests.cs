using Application.AI.Common.Interfaces.Routing;
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

/// <summary>
/// Regression coverage for the solution-review finding that CRAG weak-chunk filtering
/// was dead code: the prompt instructs the LLM to emit snake_case <c>weak_chunk_ids</c>,
/// but the response DTO bound <c>WeakChunkIds</c> with only case-insensitive matching,
/// which does not bridge the underscores — so <see cref="Domain.AI.RAG.Models.CragEvaluation.WeakChunkIds"/>
/// was always empty. The fix adds <c>[JsonPropertyName("weak_chunk_ids")]</c> to the DTO.
/// </summary>
public sealed class CragEvaluatorSolutionReviewFixTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();

    private CragEvaluator CreateEvaluator()
    {
        var config = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.Crag.AcceptThreshold = 0.7;
            c.AI.Rag.Crag.RefineThreshold = 0.4;
            c.AI.Rag.Crag.AllowWebFallback = false;
        });

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
    public async Task EvaluateAsync_SnakeCaseWeakChunkIds_BindsToWeakChunkIds()
    {
        // Arrange — the LLM follows the prompt's instructed snake_case format.
        SetupChatResponse(
            """{"action":"Refine","score":0.55,"reasoning":"partial","weak_chunk_ids":["chunk-2","chunk-3"]}""");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(3);

        // Act
        var evaluation = await evaluator.EvaluateAsync("test query", results);

        // Assert — before the fix this was always empty; now it binds the snake_case array.
        evaluation.WeakChunkIds.Should().BeEquivalentTo(["chunk-2", "chunk-3"]);
    }

    [Fact]
    public async Task EvaluateAsync_NoWeakChunkIds_ReturnsEmptyList()
    {
        // Arrange
        SetupChatResponse(
            """{"action":"Accept","score":0.9,"reasoning":"all relevant","weak_chunk_ids":[]}""");
        var evaluator = CreateEvaluator();
        var results = RagTestData.CreateRetrievalResults(2);

        // Act
        var evaluation = await evaluator.EvaluateAsync("test query", results);

        // Assert
        evaluation.WeakChunkIds.Should().BeEmpty();
    }
}

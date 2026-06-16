using System.Globalization;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.RAG.Models;
using Domain.Common.Config.AI;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Regression tests for the culture-sensitive score parsing fix (solution review finding 41).
/// <para>
/// <see cref="CrossEncoderReranker"/> previously parsed the LLM's relevance score with
/// <c>double.TryParse(trimmed, out var score)</c> — no <see cref="IFormatProvider"/>. On a host whose
/// current culture uses ',' as the decimal separator (de-DE, fr-FR, ...), '.' is treated as a group
/// separator, so the LLM's "0.8" parsed as 8.0 and clamped to 1.0 (every chunk got the maximum
/// score, collapsing the ranking). The fix pins parsing to <see cref="CultureInfo.InvariantCulture"/>.
/// </para>
/// </summary>
public sealed class CrossEncoderRerankerSolutionReviewFixTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();

    private CrossEncoderReranker CreateReranker(IDictionary<string, string> scoresByContent)
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<ChatMessage> messages, ChatOptions _, CancellationToken __) =>
            {
                // Each chunk is scored independently; return the score keyed by the chunk content
                // embedded in the prompt, defaulting to "0.5" when not matched.
                var prompt = messages[0].Text ?? string.Empty;
                var score = scoresByContent
                    .FirstOrDefault(kvp => prompt.Contains(kvp.Key, StringComparison.Ordinal)).Value ?? "0.5";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, score));
            });

        _mockRouter
            .Setup(r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelRoutingDecision
            {
                Client = mockChatClient.Object,
                SelectedTier = new ModelTier
                {
                    Name = "standard",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI,
                    DeploymentName = "gpt-4o"
                },
                Complexity = TaskComplexity.Moderate,
                Source = ClassificationSource.Heuristic,
                Confidence = 1.0,
            });

        return new CrossEncoderReranker(_mockRouter.Object, NullLogger<CrossEncoderReranker>.Instance);
    }

    [Fact]
    public async Task RerankAsync_CommaDecimalCulture_ParsesDottedScoresInvariantly()
    {
        // Arrange — three chunks with distinct dotted scores; under the old culture-sensitive parse
        // de-DE would read "0.8"/"0.6"/"0.2" as 8/6/2 and clamp all to 1.0 (ranking collapses).
        var results = new List<RetrievalResult>
        {
            RagTestData.CreateRetrievalResult(id: "low", content: "CHUNK_LOW"),
            RagTestData.CreateRetrievalResult(id: "mid", content: "CHUNK_MID"),
            RagTestData.CreateRetrievalResult(id: "high", content: "CHUNK_HIGH"),
        };

        var reranker = CreateReranker(new Dictionary<string, string>
        {
            ["CHUNK_LOW"] = "0.2",
            ["CHUNK_MID"] = "0.6",
            ["CHUNK_HIGH"] = "0.8",
        });

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // Act
            var reranked = await reranker.RerankAsync("query", results, topK: 3);

            // Assert — scores must be the invariant-parsed fractional values, not clamped to 1.0,
            // and the ranking must reflect the distinct scores rather than collapsing to ties.
            reranked.Should().HaveCount(3);
            reranked[0].RetrievalResult.Chunk.Id.Should().Be("high");
            reranked.Single(r => r.RetrievalResult.Chunk.Id == "high").RerankScore.Should().BeApproximately(0.8, 1e-9);
            reranked.Single(r => r.RetrievalResult.Chunk.Id == "mid").RerankScore.Should().BeApproximately(0.6, 1e-9);
            reranked.Single(r => r.RetrievalResult.Chunk.Id == "low").RerankScore.Should().BeApproximately(0.2, 1e-9);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}

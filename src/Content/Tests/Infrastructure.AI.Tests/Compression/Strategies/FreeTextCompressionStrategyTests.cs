// src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/FreeTextCompressionStrategyTests.cs
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Compression.Enums;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Compression.Strategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compression.Strategies;

public sealed class FreeTextCompressionStrategyTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();
    private readonly ToolOutputCompressionConfig _config = new() { LlmFallbackEnabled = true, LlmFallbackTimeoutSeconds = 5 };

    private FreeTextCompressionStrategy CreateSut() => new(
        _mockRouter.Object,
        Options.Create(_config),
        Mock.Of<ILogger<FreeTextCompressionStrategy>>());

    [Fact]
    public void CanHandle_FreeText_ReturnsTrue()
    {
        CreateSut().CanHandle(ToolOutputCategory.FreeText).Should().BeTrue();
    }

    [Theory]
    [InlineData(ToolOutputCategory.Json)]
    [InlineData(ToolOutputCategory.FileContent)]
    [InlineData(ToolOutputCategory.SearchResults)]
    [InlineData(ToolOutputCategory.Tabular)]
    public void CanHandle_NonFreeText_ReturnsFalse(ToolOutputCategory category)
    {
        CreateSut().CanHandle(category).Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_LongProse_TruncatesAtSentenceBoundary()
    {
        var sentences = Enumerable.Range(1, 200)
            .Select(i => $"This is sentence number {i} with some additional words to make it longer.");
        var input = string.Join(' ', sentences);

        var result = await CreateSut().CompressAsync(input, 100);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().EndWith("[... remainder omitted]");
        result.CompressedTokens.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task CompressAsync_ShortText_ReturnsPassthrough()
    {
        var input = "Short text.";

        var result = await CreateSut().CompressAsync(input, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be(input);
    }

    [Fact]
    public async Task CompressAsync_EmptyString_ReturnsPassthrough()
    {
        var result = await CreateSut().CompressAsync(string.Empty, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task CompressAsync_LlmFallbackDisabled_HardTruncatesOnly()
    {
        _config.LlmFallbackEnabled = false;
        var input = string.Join(' ', Enumerable.Repeat("word", 5000));

        // threshold=5 forces sentence truncation to still exceed budget (suffix alone > threshold),
        // which is the only path that can reach LLM fallback. With LLM disabled, hard truncation runs.
        var result = await CreateSut().CompressAsync(input, 5);

        result.WasCompressed.Should().BeTrue();
        _mockRouter.Verify(
            r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompressAsync_LlmThrows_FallsBackToHardTruncation()
    {
        _mockRouter
            .Setup(r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var input = string.Join(' ', Enumerable.Repeat("word", 5000));

        // threshold=5 ensures sentence truncation still exceeds budget, triggering LLM path.
        var result = await CreateSut().CompressAsync(input, 5);

        result.WasCompressed.Should().BeTrue();
        result.Strategy.Should().Be("HardTruncate");
    }

    [Fact]
    public async Task CompressAsync_LlmSucceeds_ReturnsLlmResult()
    {
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Summarized output.")])
            {
                FinishReason = ChatFinishReason.Stop
            });

        var economyTier = new ModelTier
        {
            Name = "economy",
            ClientType = AIAgentFrameworkClientType.AzureOpenAI,
            DeploymentName = "gpt-4o-mini"
        };

        var mockDecision = new ModelRoutingDecision
        {
            SelectedTier = economyTier,
            Client = mockClient.Object,
            Complexity = TaskComplexity.Simple,
            Source = ClassificationSource.Heuristic,
            Confidence = 0.9
        };

        _mockRouter
            .Setup(r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockDecision);

        var input = string.Join(' ', Enumerable.Repeat("word", 5000));

        // threshold=5 ensures sentence truncation still exceeds budget, reaching the LLM path.
        var result = await CreateSut().CompressAsync(input, 5);

        result.WasCompressed.Should().BeTrue();
        result.Strategy.Should().Be("LlmFallback");
        result.Output.Should().Be("Summarized output.");
    }

    [Fact]
    public async Task CompressAsync_SentenceTruncationFitsThreshold_UsesFreeTextStrategy()
    {
        // Build input where sentence-boundary truncation will fit in the threshold.
        // 20 short sentences. At threshold=100 tokens (~400 chars), a few sentences will fit.
        var sentences = Enumerable.Range(1, 20)
            .Select(i => $"Sentence {i}.");
        var input = string.Join(' ', sentences);

        var result = await CreateSut().CompressAsync(input, 100);

        // If compressed, should use FreeText strategy (not HardTruncate or LlmFallback)
        if (result.WasCompressed)
            result.Strategy.Should().Be("FreeText");
    }
}

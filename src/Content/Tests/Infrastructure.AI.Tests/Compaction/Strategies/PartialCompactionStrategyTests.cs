using Application.AI.Common.Interfaces;
using Domain.AI.Compaction;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Compaction.Strategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compaction.Strategies;

/// <summary>
/// Tests for <see cref="PartialCompactionStrategy"/> covering pivot-based splitting,
/// LLM summarization of older messages, and error handling.
/// </summary>
public sealed class PartialCompactionStrategyTests
{
    private readonly Mock<IChatClientFactory> _chatClientFactory;
    private readonly Mock<IChatClient> _chatClient;
    private readonly PartialCompactionStrategy _sut;

    public PartialCompactionStrategyTests()
    {
        _chatClientFactory = new Mock<IChatClientFactory>();
        _chatClient = new Mock<IChatClient>();

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI,
                    DefaultDeployment = "gpt-4o"
                }
            }
        };

        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        _chatClientFactory
            .Setup(x => x.GetChatClientAsync(
                AIAgentFrameworkClientType.AzureOpenAI,
                "gpt-4o",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chatClient.Object);

        _sut = new PartialCompactionStrategy(
            _chatClientFactory.Object,
            options,
            NullLogger<PartialCompactionStrategy>.Instance);
    }

    [Fact]
    public void Strategy_ReturnsPartial()
    {
        _sut.Strategy.Should().Be(CompactionStrategy.Partial);
    }

    [Fact]
    public async Task ExecuteAsync_TooFewMessages_ReturnsFailure()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Not enough messages");
    }

    [Fact]
    public async Task ExecuteAsync_SummarizesOnlyOlderHalf()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "First question"),
            new(ChatRole.Assistant, "First answer"),
            new(ChatRole.User, "Second question"),
            new(ChatRole.Assistant, "Second answer")
        };

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of first half."));

        _chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        await _sut.ExecuteAsync("agent-1", messages);

        _chatClient.Verify(
            x => x.GetResponseAsync(
                It.Is<IList<ChatMessage>>(m =>
                    m.Count == 3 && // system prompt + 2 older messages (first half of 4)
                    m[0].Role == ChatRole.System),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesBoundaryWithPartialStrategy()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('x', 400)),
            new(ChatRole.Assistant, new string('y', 400))
        };

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary."));

        _chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary.Should().NotBeNull();
        result.Boundary!.Strategy.Should().Be(CompactionStrategy.Partial);
        result.Boundary.PreCompactionTokens.Should().BeGreaterThan(0);
        result.Boundary.PostCompactionTokens.Should().BeGreaterThan(0);
        result.Boundary.Summary.Should().Be("Summary.");
        result.Boundary.Id.Should().NotBeNullOrEmpty();
        result.Boundary.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_OnLLMError_ReturnsFailure()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Q1"),
            new(ChatRole.Assistant, "A1")
        };

        _chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Timeout"));

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Timeout");
        result.Boundary.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResponse_SucceedsWithEmptySummary()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Q"),
            new(ChatRole.Assistant, "A")
        };

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

        _chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary!.Summary.Should().BeEmpty();
    }
}

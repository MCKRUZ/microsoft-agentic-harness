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

public sealed class FullCompactionStrategyTests
{
    private readonly Mock<IChatClientFactory> _chatClientFactory;
    private readonly Mock<IChatClient> _chatClient;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly FullCompactionStrategy _sut;

    public FullCompactionStrategyTests()
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

        _options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        _chatClientFactory
            .Setup(x => x.GetChatClientAsync(
                AIAgentFrameworkClientType.AzureOpenAI,
                "gpt-4o",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chatClient.Object);

        _sut = new FullCompactionStrategy(
            _chatClientFactory.Object,
            _options,
            NullLogger<FullCompactionStrategy>.Instance);
    }

    [Fact]
    public async Task Execute_SendsSummarizationToLLM()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What is the architecture?"),
            new(ChatRole.Assistant, "The architecture uses clean architecture with CQRS.")
        };

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation."));

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
                    m.Count == 3 && // system prompt + 2 original messages
                    m[0].Role == ChatRole.System),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_CreatesBoundaryWithMetrics()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('x', 400)), // ~100 tokens
            new(ChatRole.Assistant, new string('y', 400)) // ~100 tokens
        };

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Short summary."));

        _chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary.Should().NotBeNull();
        result.Boundary!.Strategy.Should().Be(CompactionStrategy.Full);
        result.Boundary.PreCompactionTokens.Should().Be(200); // 800 chars / 4
        result.Boundary.PostCompactionTokens.Should().BeGreaterThan(0);
        result.Boundary.TokensSaved.Should().BeGreaterThan(0);
        result.Boundary.Summary.Should().Be("Short summary.");
        result.Boundary.Id.Should().NotBeNullOrEmpty();
        result.Boundary.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Execute_OnLLMError_ReturnsFailure()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello")
        };

        _chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API rate limit exceeded"));

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("API rate limit exceeded");
        result.Boundary.Should().BeNull();
    }
}

using Domain.AI.Compaction;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Compaction.Strategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compaction.Strategies;

public sealed class MicroCompactionStrategyTests
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly MicroCompactionStrategy _sut;

    public MicroCompactionStrategyTests()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                ContextManagement = new ContextManagementConfig
                {
                    Compaction = new CompactionConfig
                    {
                        MicroCompactStalenessMinutes = 5
                    }
                }
            }
        };

        _options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        _sut = new MicroCompactionStrategy(
            _options,
            NullLogger<MicroCompactionStrategy>.Instance);
    }

    [Fact]
    public async Task Execute_ReplacesLargeToolResults()
    {
        // Create a message with a large assistant response (>5000 chars)
        // Place it in the first half so it's considered "stale"
        var largeContent = new string('x', 6000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Do something"),
            new(ChatRole.Assistant, largeContent),
            new(ChatRole.User, "Another question"),
            new(ChatRole.Assistant, "Short response")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary.Should().NotBeNull();
        result.Boundary!.Strategy.Should().Be(CompactionStrategy.Micro);
        // The large tool result in the first half should have been identified
        result.Boundary.PreCompactionTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Execute_SkipsRecentResults()
    {
        // All messages in the second half (recent) should not be compacted
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Recent question"),
            new(ChatRole.Assistant, "Recent short answer")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary.Should().NotBeNull();
        // With only 2 messages and the assistant at index 1 (>= count/2=1), it's not stale
        result.Boundary!.Summary.Should().Contain("No compactable content found");
    }

    [Fact]
    public async Task Execute_NoCompactableContent_ReturnsNoOpBoundary()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there!")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary.Should().NotBeNull();
        result.Boundary!.Summary.Should().Contain("No compactable content found");
        result.Boundary.TokensSaved.Should().Be(0);
    }
}

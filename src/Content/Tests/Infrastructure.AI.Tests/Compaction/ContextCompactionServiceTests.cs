using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Compaction;
using Domain.AI.Hooks;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compaction;

public sealed class ContextCompactionServiceTests
{
    private readonly Mock<ICompactionStrategyExecutor> _fullExecutor;
    private readonly Mock<IHookExecutor> _hookExecutor;
    private readonly Mock<ISystemPromptComposer> _promptComposer;
    private readonly Mock<IAutoCompactStateMachine> _stateMachine;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ContextCompactionService _sut;

    public ContextCompactionServiceTests()
    {
        _fullExecutor = new Mock<ICompactionStrategyExecutor>();
        _fullExecutor.Setup(x => x.Strategy).Returns(CompactionStrategy.Full);

        _hookExecutor = new Mock<IHookExecutor>();
        _hookExecutor
            .Setup(x => x.ExecuteHooksAsync(It.IsAny<HookEvent>(), It.IsAny<HookExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HookResult>());

        _promptComposer = new Mock<ISystemPromptComposer>();
        _stateMachine = new Mock<IAutoCompactStateMachine>();

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                ContextManagement = new ContextManagementConfig
                {
                    Compaction = new CompactionConfig
                    {
                        AutoCompactThresholdRatio = 0.85,
                        CircuitBreakerMaxFailures = 3,
                        CircuitBreakerCooldownSeconds = 60
                    }
                }
            }
        };

        _options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        _sut = new ContextCompactionService(
            new[] { _fullExecutor.Object },
            _hookExecutor.Object,
            _promptComposer.Object,
            _stateMachine.Object,
            _options,
            NullLogger<ContextCompactionService>.Instance);
    }

    [Fact]
    public async Task CompactAsync_DelegatesCorrectStrategy()
    {
        var boundary = new CompactionBoundaryMessage
        {
            Id = "test",
            Trigger = CompactionTrigger.Manual,
            Strategy = CompactionStrategy.Full,
            PreCompactionTokens = 1000,
            PostCompactionTokens = 200,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "Summary"
        };

        _fullExecutor
            .Setup(x => x.ExecuteAsync("agent-1", It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompactionResult.Succeeded(boundary));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.CompactAsync("agent-1", messages, CompactionStrategy.Full);

        result.Success.Should().BeTrue();
        result.Boundary.Should().Be(boundary);
        _fullExecutor.Verify(x => x.ExecuteAsync("agent-1", messages, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompactAsync_FiresPreAndPostCompactHooks()
    {
        var boundary = new CompactionBoundaryMessage
        {
            Id = "test",
            Trigger = CompactionTrigger.Manual,
            Strategy = CompactionStrategy.Full,
            PreCompactionTokens = 1000,
            PostCompactionTokens = 200,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "Summary"
        };

        _fullExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompactionResult.Succeeded(boundary));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await _sut.CompactAsync("agent-1", messages, CompactionStrategy.Full);

        _hookExecutor.Verify(
            x => x.ExecuteHooksAsync(
                HookEvent.PreCompact,
                It.Is<HookExecutionContext>(c => c.AgentId == "agent-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _hookExecutor.Verify(
            x => x.ExecuteHooksAsync(
                HookEvent.PostCompact,
                It.Is<HookExecutionContext>(c => c.AgentId == "agent-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompactAsync_OnSuccess_InvalidatesPromptCache()
    {
        var boundary = new CompactionBoundaryMessage
        {
            Id = "test",
            Trigger = CompactionTrigger.Manual,
            Strategy = CompactionStrategy.Full,
            PreCompactionTokens = 1000,
            PostCompactionTokens = 200,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "Summary"
        };

        _fullExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompactionResult.Succeeded(boundary));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await _sut.CompactAsync("agent-1", messages, CompactionStrategy.Full);

        _promptComposer.Verify(x => x.InvalidateAll(), Times.Once);
    }

    [Fact]
    public async Task CompactAsync_OnFailure_RecordsFailure()
    {
        _fullExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompactionResult.Failed("LLM error"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.CompactAsync("agent-1", messages, CompactionStrategy.Full);

        result.Success.Should().BeFalse();
        _stateMachine.Verify(x => x.RecordFailure("agent-1"), Times.Once);
        _stateMachine.Verify(x => x.RecordSuccess(It.IsAny<string>()), Times.Never);
        _promptComposer.Verify(x => x.InvalidateAll(), Times.Never);
    }

    [Fact]
    public void ShouldAutoCompact_BelowThreshold_ReturnsFalse()
    {
        _stateMachine.Setup(x => x.IsCircuitBroken("agent-1")).Returns(false);

        // 80% of 10000 = 8000, threshold is 85% = 8500
        var result = _sut.ShouldAutoCompact("agent-1", 8000, 10000);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoCompact_AboveThreshold_ReturnsTrue()
    {
        _stateMachine.Setup(x => x.IsCircuitBroken("agent-1")).Returns(false);

        // 90% of 10000 = 9000, threshold is 85% = 8500
        var result = _sut.ShouldAutoCompact("agent-1", 9000, 10000);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoCompact_CircuitBroken_ReturnsFalse()
    {
        _stateMachine.Setup(x => x.IsCircuitBroken("agent-1")).Returns(true);

        var result = _sut.ShouldAutoCompact("agent-1", 9000, 10000);

        result.Should().BeFalse();
    }
}

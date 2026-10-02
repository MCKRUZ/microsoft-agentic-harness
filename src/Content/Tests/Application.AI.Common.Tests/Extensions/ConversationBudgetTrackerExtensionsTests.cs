using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.MediatR;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Extensions;

/// <summary>
/// <see cref="ConversationBudgetTrackerExtensions.RecordTurnUsageAsync"/> (#778): every way a turn can
/// end is charged, never under the caller's token, and an accrual error never replaces the outcome of a
/// turn that already failed.
/// </summary>
public sealed class ConversationBudgetTrackerExtensionsTests
{
    private readonly Mock<IConversationBudgetTracker> _tracker = new();
    private readonly RecordingLogger _logger = new();

    private static IAgentTurnResult Turn(bool success) =>
        Mock.Of<IAgentTurnResult>(r => r.Success == success && r.InputTokens == 900 && r.OutputTokens == 100);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordTurnUsageAsync_ChargesInputPlusOutput_WhateverTheOutcome(bool success)
    {
        await _tracker.Object.RecordTurnUsageAsync("c1", Turn(success), _logger);

        _tracker.Verify(t => t.RecordUsageAsync("c1", 1000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordTurnUsageAsync_NeverRunsTheAccrualUnderACancellableToken()
    {
        // A client that disconnects cancels the token the turn ran under; the same token on the write
        // would abandon the spend of exactly the turn that was cut short.
        CancellationToken observed = new(canceled: true);
        _tracker
            .Setup(t => t.RecordUsageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((string _, int _, CancellationToken ct) => observed = ct)
            .Returns(Task.CompletedTask);

        await _tracker.Object.RecordTurnUsageAsync("c1", Turn(success: false), _logger);

        observed.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task RecordTurnUsageAsync_TrackerFailsOnAFailedTurn_LogsAndLetsTheOutcomeStand()
    {
        _tracker
            .Setup(t => t.RecordUsageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("budget store unavailable"));

        var act = () => _tracker.Object.RecordTurnUsageAsync("c1", Turn(success: false), _logger);

        await act.Should().NotThrowAsync(
            "the caller must still see the failure or cancellation, not a tracker error that replaced it");
        _logger.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task RecordTurnUsageAsync_TrackerFailsOnASuccessfulTurn_StillPropagates()
    {
        _tracker
            .Setup(t => t.RecordUsageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("budget store unavailable"));

        var act = () => _tracker.Object.RecordTurnUsageAsync("c1", Turn(success: true), _logger);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }
}

using Application.AI.Common.Exceptions;
using Application.AI.Common.Services.Context;
using Domain.AI.Context;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Context;

public class ContextBudgetTrackerTests
{
    private readonly ContextBudgetTracker _tracker;
    private readonly Mock<IOptionsMonitor<AppConfig>> _optionsMock;
    private readonly AppConfig _appConfig;

    public ContextBudgetTrackerTests()
    {
        _appConfig = new AppConfig();
        _optionsMock = new Mock<IOptionsMonitor<AppConfig>>();
        _optionsMock.Setup(o => o.CurrentValue).Returns(_appConfig);

        _tracker = new ContextBudgetTracker(
            _optionsMock.Object,
            NullLogger<ContextBudgetTracker>.Instance);
    }

    [Fact]
    public void RecordAllocation_NewAgent_TracksTokens()
    {
        _tracker.RecordAllocation("agent-1", "system_prompt", 500);

        _tracker.GetTotalAllocated("agent-1").Should().Be(500);
    }

    [Fact]
    public void RecordAllocation_MultipleComponents_SumsTokens()
    {
        _tracker.RecordAllocation("agent-1", "system_prompt", 500);
        _tracker.RecordAllocation("agent-1", "tools", 300);

        _tracker.GetTotalAllocated("agent-1").Should().Be(800);
    }

    [Fact]
    public void RecordAllocation_SameComponentMultipleTimes_Accumulates()
    {
        _tracker.RecordAllocation("agent-1", "history", 100);
        _tracker.RecordAllocation("agent-1", "history", 200);

        _tracker.GetTotalAllocated("agent-1").Should().Be(300);
    }

    [Fact]
    public void RecordAllocation_NullAgentName_ThrowsArgumentException()
    {
        var act = () => _tracker.RecordAllocation(null!, "component", 100);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecordAllocation_NegativeTokens_ThrowsArgumentOutOfRangeException()
    {
        var act = () => _tracker.RecordAllocation("agent-1", "component", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetTotalAllocated_UnknownAgent_ReturnsZero()
    {
        _tracker.GetTotalAllocated("unknown").Should().Be(0);
    }

    [Fact]
    public void GetRemainingBudget_UnderBudget_ReturnsCorrectRemaining()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 3000);

        var remaining = _tracker.GetRemainingBudget("agent-1", 10000);

        remaining.Should().Be(7000);
    }

    [Fact]
    public void GetRemainingBudget_OverBudget_ReturnsZero()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 15000);

        var remaining = _tracker.GetRemainingBudget("agent-1", 10000);

        remaining.Should().Be(0);
    }

    [Fact]
    public void GetRemainingBudget_At80Percent_LogsWarning()
    {
        var loggerMock = new Mock<ILogger<ContextBudgetTracker>>();
        var tracker = new ContextBudgetTracker(_optionsMock.Object, loggerMock.Object);

        tracker.RecordAllocation("agent-1", "prompt", 8500);

        tracker.GetRemainingBudget("agent-1", 10000);

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void GetRemainingBudget_Under80Percent_NoWarningLogged()
    {
        var loggerMock = new Mock<ILogger<ContextBudgetTracker>>();
        var tracker = new ContextBudgetTracker(_optionsMock.Object, loggerMock.Object);

        tracker.RecordAllocation("agent-1", "prompt", 5000);

        tracker.GetRemainingBudget("agent-1", 10000);

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public void EnsureBudget_WithinBudget_DoesNotThrow()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 5000);

        var act = () => _tracker.EnsureBudget("agent-1", 3000, 10000);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBudget_ExceedsBudget_ThrowsContextBudgetExceededException()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 9000);

        var act = () => _tracker.EnsureBudget("agent-1", 2000, 10000);

        act.Should().Throw<ContextBudgetExceededException>();
    }

    [Fact]
    public void Reset_ClearsAllAllocations()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 5000);
        _tracker.RecordAllocation("agent-1", "tools", 3000);

        _tracker.Reset("agent-1");

        _tracker.GetTotalAllocated("agent-1").Should().Be(0);
    }

    [Fact]
    public void Reset_DoesNotAffectOtherAgents()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 5000);
        _tracker.RecordAllocation("agent-2", "prompt", 3000);

        _tracker.Reset("agent-1");

        _tracker.GetTotalAllocated("agent-1").Should().Be(0);
        _tracker.GetTotalAllocated("agent-2").Should().Be(3000);
    }

    [Fact]
    public void GetBreakdown_ReturnsComponentBreakdown()
    {
        _tracker.RecordAllocation("agent-1", "system_prompt", 500);
        _tracker.RecordAllocation("agent-1", "tools", 300);

        var breakdown = _tracker.GetBreakdown("agent-1");

        breakdown.Should().ContainKey("system_prompt").WhoseValue.Should().Be(500);
        breakdown.Should().ContainKey("tools").WhoseValue.Should().Be(300);
    }

    [Fact]
    public void GetBreakdown_UnknownAgent_ReturnsEmptyDictionary()
    {
        var breakdown = _tracker.GetBreakdown("unknown");

        breakdown.Should().BeEmpty();
    }

    [Fact]
    public void AssessContinuation_HealthyBudget_ReturnsContinue()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 5000);

        var assessment = _tracker.AssessContinuation("agent-1", 100000);

        assessment.Action.Should().Be(TokenBudgetAction.Continue);
    }

    [Fact]
    public void AssessContinuation_At80Percent_ReturnsNudge()
    {
        _tracker.RecordAllocation("agent-1", "prompt", 85000);

        var assessment = _tracker.AssessContinuation("agent-1", 100000);

        assessment.Action.Should().Be(TokenBudgetAction.Nudge);
    }

    [Fact]
    public void AssessContinuation_AboveCompletionThreshold_ReturnsStop()
    {
        // Default CompletionThresholdRatio = 0.90
        _tracker.RecordAllocation("agent-1", "prompt", 95000);

        var assessment = _tracker.AssessContinuation("agent-1", 100000);

        assessment.Action.Should().Be(TokenBudgetAction.Stop);
    }

    [Fact]
    public void RecordContinuation_TracksContinuationCount()
    {
        _tracker.RecordContinuation("agent-1", 1000);
        _tracker.RecordContinuation("agent-1", 800);

        var assessment = _tracker.AssessContinuation("agent-1", 100000);

        assessment.ContinuationCount.Should().Be(2);
    }

    [Fact]
    public void AssessContinuation_DiminishingReturns_ReturnsStop()
    {
        // Default: threshold=3 continuations, minDelta=500
        _tracker.RecordContinuation("agent-1", 1000);
        _tracker.RecordContinuation("agent-1", 600);
        _tracker.RecordContinuation("agent-1", 100); // below 500
        _tracker.RecordContinuation("agent-1", 50);  // below 500, previous also below

        var assessment = _tracker.AssessContinuation("agent-1", 100000);

        assessment.Action.Should().Be(TokenBudgetAction.Stop);
        assessment.Reason.Should().Contain("Diminishing returns");
    }

    [Fact]
    public void ConcurrentAccess_MultipleAgents_ThreadSafe()
    {
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() =>
            {
                var agentName = $"agent-{i % 10}";
                _tracker.RecordAllocation(agentName, $"component-{i}", 100);
            }));

        var act = () => Task.WhenAll(tasks).Wait();

        act.Should().NotThrow();

        // Each of the 10 agents should have received allocations from 10 tasks
        for (var i = 0; i < 10; i++)
        {
            _tracker.GetTotalAllocated($"agent-{i}").Should().Be(1000);
        }
    }
}

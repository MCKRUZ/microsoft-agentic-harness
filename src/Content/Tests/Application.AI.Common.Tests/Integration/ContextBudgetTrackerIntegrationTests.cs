using Application.AI.Common.Exceptions;
using Application.AI.Common.Services.Context;
using Domain.AI.Context;
using Domain.Common.Config;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="ContextBudgetTracker"/> exercising multi-agent
/// concurrent access, diminishing returns detection, and budget assessment workflows.
/// </summary>
public class ContextBudgetTrackerIntegrationTests
{
    private readonly Mock<IOptionsMonitor<AppConfig>> _optionsMock;

    public ContextBudgetTrackerIntegrationTests()
    {
        _optionsMock = new Mock<IOptionsMonitor<AppConfig>>();
        _optionsMock.Setup(o => o.CurrentValue).Returns(new AppConfig());
    }

    private ContextBudgetTracker CreateTracker() =>
        new(_optionsMock.Object, NullLogger<ContextBudgetTracker>.Instance);

    [Fact]
    public void MultiAgentScenario_IndependentTracking_DoNotInterfere()
    {
        var tracker = CreateTracker();

        tracker.RecordAllocation("planner", "system_prompt", 2000);
        tracker.RecordAllocation("planner", "tools", 500);
        tracker.RecordAllocation("coder", "system_prompt", 3000);
        tracker.RecordAllocation("coder", "tools", 1000);
        tracker.RecordAllocation("reviewer", "system_prompt", 1500);

        tracker.GetTotalAllocated("planner").Should().Be(2500);
        tracker.GetTotalAllocated("coder").Should().Be(4000);
        tracker.GetTotalAllocated("reviewer").Should().Be(1500);
    }

    [Fact]
    public void MultiAgentScenario_ResetOne_DoesNotAffectOthers()
    {
        var tracker = CreateTracker();

        tracker.RecordAllocation("agent-a", "prompt", 5000);
        tracker.RecordAllocation("agent-b", "prompt", 3000);

        tracker.Reset("agent-a");

        tracker.GetTotalAllocated("agent-a").Should().Be(0);
        tracker.GetTotalAllocated("agent-b").Should().Be(3000);
        tracker.GetBreakdown("agent-a").Should().BeEmpty();
    }

    [Fact]
    public void BudgetAssessment_FullLifecycle_ProgressesFromContinueToStop()
    {
        var tracker = CreateTracker();
        var budget = 100_000;

        // Phase 1: Healthy - below 80%
        tracker.RecordAllocation("agent", "prompt", 50_000);
        var assessment = tracker.AssessContinuation("agent", budget);
        assessment.Action.Should().Be(TokenBudgetAction.Continue);

        // Phase 2: Warning - at 80%
        tracker.RecordAllocation("agent", "tools", 30_000);
        assessment = tracker.AssessContinuation("agent", budget);
        assessment.Action.Should().Be(TokenBudgetAction.Nudge);
        assessment.Reason.Should().Contain("approaching limit");

        // Phase 3: Stop - above 90% (default CompletionThresholdRatio)
        tracker.RecordAllocation("agent", "history", 15_000);
        assessment = tracker.AssessContinuation("agent", budget);
        assessment.Action.Should().Be(TokenBudgetAction.Stop);
        assessment.Reason.Should().Contain("threshold");
    }

    [Fact]
    public void DiminishingReturns_AfterThresholdContinuations_DetectsAndStops()
    {
        var tracker = CreateTracker();

        // Productive continuations
        tracker.RecordContinuation("agent", 2000);
        tracker.RecordContinuation("agent", 1500);
        tracker.RecordContinuation("agent", 1000);

        // Diminishing continuations (below default minDelta of 500)
        tracker.RecordContinuation("agent", 200);
        tracker.RecordContinuation("agent", 100);

        var assessment = tracker.AssessContinuation("agent", 100_000);

        assessment.Action.Should().Be(TokenBudgetAction.Stop);
        assessment.Reason.Should().Contain("Diminishing returns");
        assessment.ContinuationCount.Should().Be(5);
    }

    [Fact]
    public void DiminishingReturns_StillProductive_DoesNotStop()
    {
        var tracker = CreateTracker();

        tracker.RecordContinuation("agent", 2000);
        tracker.RecordContinuation("agent", 1500);
        tracker.RecordContinuation("agent", 1000);
        tracker.RecordContinuation("agent", 800); // above 500 threshold

        var assessment = tracker.AssessContinuation("agent", 100_000);

        assessment.Action.Should().Be(TokenBudgetAction.Continue);
    }

    [Fact]
    public void ComponentBreakdown_DetailedAllocation_ReturnsPerComponentTotals()
    {
        var tracker = CreateTracker();

        tracker.RecordAllocation("agent", "system_prompt", 2000);
        tracker.RecordAllocation("agent", "tool_schemas", 500);
        tracker.RecordAllocation("agent", "conversation_history", 3000);
        tracker.RecordAllocation("agent", "skill_context", 1000);
        tracker.RecordAllocation("agent", "conversation_history", 2000); // accumulates

        var breakdown = tracker.GetBreakdown("agent");

        breakdown.Should().HaveCount(4);
        breakdown["system_prompt"].Should().Be(2000);
        breakdown["tool_schemas"].Should().Be(500);
        breakdown["conversation_history"].Should().Be(5000);
        breakdown["skill_context"].Should().Be(1000);
    }

    [Fact]
    public void EnsureBudget_ExactlyAtBudget_DoesNotThrow()
    {
        var tracker = CreateTracker();

        tracker.RecordAllocation("agent", "prompt", 8000);

        var act = () => tracker.EnsureBudget("agent", 2000, 10000);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBudget_OneTokenOver_ThrowsContextBudgetExceeded()
    {
        var tracker = CreateTracker();

        tracker.RecordAllocation("agent", "prompt", 8000);

        var act = () => tracker.EnsureBudget("agent", 2001, 10000);

        act.Should().Throw<ContextBudgetExceededException>();
    }

    [Fact]
    public void CustomBudgetConfig_OverrideThresholds_RespectedByAssessment()
    {
        var config = new AppConfig();
        config.AI.ContextManagement.Budget.CompletionThresholdRatio = 0.70;
        config.AI.ContextManagement.Budget.DiminishingReturnsContinuationThreshold = 2;
        config.AI.ContextManagement.Budget.DiminishingReturnsMinDelta = 1000;

        var optionsMock = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMock.Setup(o => o.CurrentValue).Returns(config);

        var tracker = new ContextBudgetTracker(
            optionsMock.Object, NullLogger<ContextBudgetTracker>.Instance);

        // At 75% - should stop because threshold is 70%
        tracker.RecordAllocation("agent", "prompt", 75_000);
        var assessment = tracker.AssessContinuation("agent", 100_000);

        assessment.Action.Should().Be(TokenBudgetAction.Stop);
    }

    [Fact]
    public async Task ConcurrentAllocations_ThreadSafe_AllRecorded()
    {
        var tracker = CreateTracker();

        var tasks = Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => tracker.RecordAllocation("agent", $"component-{i}", 100)));

        await Task.WhenAll(tasks);

        tracker.GetTotalAllocated("agent").Should().Be(5000);
        tracker.GetBreakdown("agent").Should().HaveCount(50);
    }

    [Fact]
    public void RemainingBudget_ZeroBudget_ReturnsZero()
    {
        var tracker = CreateTracker();

        tracker.GetRemainingBudget("agent", 0).Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsContinuationState_AssessmentResets()
    {
        var tracker = CreateTracker();

        tracker.RecordContinuation("agent", 100);
        tracker.RecordContinuation("agent", 50);

        tracker.Reset("agent");

        var assessment = tracker.AssessContinuation("agent", 100_000);
        assessment.ContinuationCount.Should().Be(0);
    }
}

using Application.AI.Common.Evaluation.Governance;
using Domain.AI.Changes;
using Domain.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Evaluation.Governance;

/// <summary>
/// The load-bearing test in this file is <see cref="IsEngaged_is_false_for_the_shared_Empty_singleton"/>:
/// it is the exact object every ungoverned production run hands to a judge rubric, so if
/// this ever regresses to true, every "did governance actually run" check downstream goes
/// silently blind.
/// </summary>
public sealed class GovernanceTraceRendererTests
{
    [Fact]
    public void IsEngaged_is_false_for_the_shared_Empty_singleton()
    {
        GovernanceTraceRenderer.IsEngaged(GovernanceTrace.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsEngaged_is_false_for_a_fresh_trace_with_no_decisions()
    {
        // Not the singleton (record equality, not reference equality, is what must matter) —
        // proves the check is content-based, not identity-based.
        var trace = new GovernanceTrace();

        GovernanceTraceRenderer.IsEngaged(trace).Should().BeFalse();
    }

    [Fact]
    public void IsEngaged_is_true_when_enforcement_on_with_zero_tool_calls()
    {
        var trace = new GovernanceTrace { EnforcementEnabled = true };

        GovernanceTraceRenderer.IsEngaged(trace).Should().BeTrue(
            "enforcement being on with nothing to gate is still a real, gradeable governed run");
    }

    [Fact]
    public void IsEngaged_is_true_when_at_least_one_tool_call_was_recorded()
    {
        var trace = new GovernanceTrace
        {
            ToolDecisions =
            [
                new ToolDecisionRecord("write_file", ToolDecisionOutcome.Denied, "matched deny rule",
                    BlastRadius.High, RequiredApproval: true, ApprovalGranted: false, Enforced: true)
            ]
        };

        GovernanceTraceRenderer.IsEngaged(trace).Should().BeTrue();
    }

    [Fact]
    public void IsEngaged_is_false_for_null()
    {
        GovernanceTraceRenderer.IsEngaged(null).Should().BeFalse();
    }

    [Fact]
    public void Render_returns_the_sentinel_when_not_engaged()
    {
        GovernanceTraceRenderer.Render(GovernanceTrace.Empty)
            .Should().Be(GovernanceTraceRenderer.NoTraceSentinel);
    }

    [Fact]
    public void Render_includes_tool_name_outcome_and_enforced_flag_for_each_decision()
    {
        var trace = new GovernanceTrace
        {
            EnforcementEnabled = true,
            ToolDecisions =
            [
                new ToolDecisionRecord("write_file", ToolDecisionOutcome.Denied, "matched deny rule secrets-to-disk",
                    BlastRadius.High, RequiredApproval: true, ApprovalGranted: false, Enforced: true)
            ]
        };

        var rendered = GovernanceTraceRenderer.Render(trace);

        rendered.Should().Contain("write_file");
        rendered.Should().Contain("Denied");
        rendered.Should().Contain("[enforced]");
        rendered.Should().Contain("matched deny rule secrets-to-disk");
    }

    [Fact]
    public void Render_caps_decisions_and_reports_the_omitted_count()
    {
        var decisions = Enumerable.Range(1, 25)
            .Select(i => new ToolDecisionRecord($"tool_{i}", ToolDecisionOutcome.Allowed, "ok",
                BlastRadius.Low, RequiredApproval: false, ApprovalGranted: false, Enforced: true))
            .ToList();
        var trace = new GovernanceTrace { EnforcementEnabled = true, ToolDecisions = decisions };

        var rendered = GovernanceTraceRenderer.Render(trace);

        rendered.Should().Contain("tool_20");
        rendered.Should().NotContain("tool_21");
        rendered.Should().Contain("5 more omitted");
    }
}

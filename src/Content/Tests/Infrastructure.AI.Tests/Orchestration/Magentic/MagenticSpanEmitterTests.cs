using System.Linq;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Microsoft.Extensions.AI;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// PR-6 acceptance tests for the Magentic span tree. Drives the
/// <see cref="MagenticEventSubscriber"/> with synthetic events and verifies the
/// resulting <see cref="System.Diagnostics.Activity"/> stream matches
/// <c>documentation/architecture/magentic-spans.md</c>.
/// </summary>
[Collection("MagenticTraceCollection")]
public sealed class MagenticSpanEmitterTests
{
    [Fact]
    public async Task PlanCreated_emits_root_and_manager_spans()
    {
        using var captured = new MagenticTestHelpers.CapturedSpans();
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var request = MagenticTestHelpers.BuildRequest();

        subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);
        var planCreated = new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("initial plan"));
        await subscriber.ProcessEventAsync(planCreated, default);
        subscriber.EndWorkflow(MagenticConventions.CompletionReasonSatisfied);

        captured.Activities.Should().Contain(a =>
            a.DisplayName.StartsWith(MagenticConventions.SpanNameWorkflowPrefix));
        captured.Activities.Should().Contain(a => a.DisplayName == MagenticConventions.SpanNameManager);
        var manager = captured.Activities.First(a => a.DisplayName == MagenticConventions.SpanNameManager);
        manager.GetTagItem(MagenticConventions.Role).Should().Be(MagenticConventions.RoleManager);
        manager.Events.Should().Contain(e => e.Name == MagenticConventions.EventPlanCreated);
    }

    [Fact]
    public async Task Progress_ledger_emits_round_span_with_counter()
    {
        using var captured = new MagenticTestHelpers.CapturedSpans();
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var request = MagenticTestHelpers.BuildRequest();
        subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);

        var ledger = MagenticTestHelpers.BuildLedger(
            isRequestSatisfied: false,
            isInLoop: false,
            isProgressBeingMade: true,
            nextSpeaker: "participant-a",
            instructionOrQuestion: "go");
        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(ledger), default);
        subscriber.EndWorkflow(MagenticConventions.CompletionReasonSatisfied);

        subscriber.RoundsExecuted.Should().Be(1);
        var round = captured.Activities.First(a => a.DisplayName.StartsWith(MagenticConventions.SpanNameRoundPrefix));
        round.GetTagItem(MagenticConventions.RoundNumber).Should().Be(1);
        round.GetTagItem(MagenticConventions.ProgressNextSpeaker).Should().Be("participant-a");
        round.Events.Should().Contain(e => e.Name == MagenticConventions.EventProgressLedgerUpdated);
    }

    [Fact]
    public async Task Replan_emits_reset_span_and_increments_plan_version()
    {
        using var captured = new MagenticTestHelpers.CapturedSpans();
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var request = MagenticTestHelpers.BuildRequest();
        subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);

        await subscriber.ProcessEventAsync(new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("plan")), default);
        await subscriber.ProcessEventAsync(new MagenticReplannedEvent(MagenticTestHelpers.AsLedger("revise plan")), default);
        subscriber.EndWorkflow(MagenticConventions.CompletionReasonSatisfied);

        subscriber.ResetsExecuted.Should().Be(1);
        var reset = captured.Activities.First(a => a.DisplayName.StartsWith(MagenticConventions.SpanNameResetPrefix));
        reset.GetTagItem(MagenticConventions.ResetNumber).Should().Be(1);
        reset.GetTagItem(MagenticConventions.ResetTrigger).Should().BeOneOf(
            MagenticConventions.ResetTriggerStall,
            MagenticConventions.ResetTriggerLedgerFailure);

        var manager = captured.Activities.First(a => a.DisplayName == MagenticConventions.SpanNameManager);
        manager.Events.Should().Contain(e => e.Name == MagenticConventions.EventReplanned);
        manager.GetTagItem(MagenticConventions.PlanVersion).Should().Be(2);
    }

    [Fact]
    public async Task Stall_counter_increments_then_decrements_with_round_quality()
    {
        using var captured = new MagenticTestHelpers.CapturedSpans();
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var request = MagenticTestHelpers.BuildRequest();
        subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);

        var stalled = MagenticTestHelpers.BuildLedger(isInLoop: true, isProgressBeingMade: false, nextSpeaker: "p", instructionOrQuestion: "again");
        var clean = MagenticTestHelpers.BuildLedger(isInLoop: false, isProgressBeingMade: true, nextSpeaker: "p", instructionOrQuestion: "next");

        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(stalled), default);
        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(stalled), default);
        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(clean), default);
        subscriber.EndWorkflow(MagenticConventions.CompletionReasonSatisfied);

        var rounds = captured.Activities
            .Where(a => a.DisplayName.StartsWith(MagenticConventions.SpanNameRoundPrefix))
            .OrderBy(a => a.GetTagItem(MagenticConventions.RoundNumber))
            .ToList();
        rounds.Should().HaveCount(3);
        rounds[0].GetTagItem(MagenticConventions.RoundStallCountAfter).Should().Be(1);
        rounds[1].GetTagItem(MagenticConventions.RoundStallCountAfter).Should().Be(2);
        rounds[2].GetTagItem(MagenticConventions.RoundStallCountAfter).Should().Be(1);
        subscriber.RoundsExecuted.Should().Be(3);
        subscriber.ResetsExecuted.Should().Be(0);
    }
}

#pragma warning restore MAAIW001

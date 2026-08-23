using System.Linq;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Infrastructure.AI.Orchestration.Magentic;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// PR-6 acceptance tests for the Magentic span tree. Drives the
/// <see cref="Infrastructure.AI.Orchestration.Magentic.MagenticEventSubscriber"/> with synthetic events and verifies the
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

    /// <summary>
    /// #470: <c>EndWorkflowSpan</c> used to redact <c>errorMessage</c> without sanitizing first, so a
    /// secret split by invisible/zero-width characters (which the sanitizer canonicalizes away, but
    /// the redaction filter's anchored patterns do not) could dodge redaction here while the identical
    /// string was caught on the tool-failure-reporting path. Proven by ordering, not by depending on
    /// the real sanitizer's exact zero-width handling: a sanitizer mock that joins a split key only
    /// shows up redacted if redaction ran against the sanitizer's output.
    /// </summary>
    [Fact]
    public void EndWorkflowSpan_SanitizesBeforeRedacting()
    {
        using var source = new System.Diagnostics.ActivitySource("test.magentic.sanitize-order");
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _)
                => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var workflowSpan = source.StartActivity("workflow-span");
        workflowSpan.Should().NotBeNull();

        var sanitizer = new Moq.Mock<Application.AI.Common.Interfaces.Governance.ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize("secret is AKIA<split>ABCDEFGHIJ123456", It.IsAny<string?>()))
            .Returns(Domain.AI.Governance.SanitizationResult.Clean("secret is AKIAABCDEFGHIJ123456"));

        MagenticSpanEmitter.EndWorkflowSpan(
            workflowSpan,
            roundsExecuted: 1,
            resetsExecuted: 0,
            completionReason: MagenticConventions.CompletionReasonError,
            errorMessage: "secret is AKIA<split>ABCDEFGHIJ123456",
            sanitizer.Object,
            new Infrastructure.AI.Telemetry.Redaction.DefaultContentRedactionFilter());

        var tag = workflowSpan!.GetTagItem(GenAiSemconvRegistry.ErrorType) as string;
        tag.Should().NotBeNull();
        tag.Should().Contain("[REDACTED:AwsKey]",
            "redaction must run against the sanitizer's output, which joined the split key back together");
        tag.Should().NotContain("AKIAABCDEFGHIJ123456");
    }
}

#pragma warning restore MAAIW001

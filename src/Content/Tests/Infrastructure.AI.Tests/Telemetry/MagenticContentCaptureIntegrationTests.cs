using System.Diagnostics;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI.Telemetry;
using FluentAssertions;
using Infrastructure.AI.Orchestration.Magentic;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// End-to-end content-capture integration tests for the Magentic span emitter.
/// This closes the PR-1 (PR-6) deferral: the
/// <see cref="MagenticSpanEmitter"/> content methods, wired to the real
/// <see cref="DefaultContentRedactionFilter"/> and a real
/// <see cref="ContentCapturePolicy"/>, must attach REDACTED content under the
/// semconv attribute keys when capture is ON, and attach NO content attribute
/// when capture is OFF. Spans/events are captured via an
/// <see cref="ActivityListener"/>, matching the established
/// <c>MagenticSpanEmitterTests</c> pattern. Attribute key names are asserted
/// from <see cref="MagenticConventions"/> (never hardcoded strings).
/// </summary>
public sealed class MagenticContentCaptureIntegrationTests
{
    private sealed class CapturedSpans : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Activities { get; } = [];

        public CapturedSpans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == MagenticConventions.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = a => Activities.Add(a),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    private static IContentCapturePolicy Policy(ContentCaptureConfig capture)
        => new ContentCapturePolicy(
            ContentCaptureTestConfig.Monitor(capture),
            NullLogger<ContentCapturePolicy>.Instance);

    private static readonly DefaultContentRedactionFilter Filter = new();

    private const string PlanWithEmail = "Step 1: email the owner alice@example.com about the plan.";

    [Fact]
    public void RecordPlanCreatedWithContent_CaptureOn_AttachesRedactedPlanContent()
    {
        using var captured = new CapturedSpans();
        using var emitter = new MagenticSpanEmitter();
        var workflow = emitter.StartWorkflowSpan("wf", null, 3, null, false, ["p"]);
        var manager = emitter.StartManagerSpan(workflow);

        MagenticSpanEmitter.RecordPlanCreatedWithContent(
            manager, planVersion: 1, planText: PlanWithEmail,
            Policy(ContentCaptureTestConfig.AllOn()), Filter);

        var evt = manager!.Events.Single(e => e.Name == MagenticConventions.EventPlanCreated);
        var content = evt.Tags.Single(t => t.Key == MagenticConventions.PlanContent).Value as string;

        content.Should().NotBeNull();
        content!.Should().NotContain("alice@example.com");
        content.Should().Contain("[REDACTED:Email]");
    }

    [Fact]
    public void RecordPlanCreatedWithContent_CaptureOff_AttachesNoContent()
    {
        using var captured = new CapturedSpans();
        using var emitter = new MagenticSpanEmitter();
        var workflow = emitter.StartWorkflowSpan("wf", null, 3, null, false, ["p"]);
        var manager = emitter.StartManagerSpan(workflow);

        var off = ContentCaptureTestConfig.AllOn();
        off.Enabled = false;
        MagenticSpanEmitter.RecordPlanCreatedWithContent(
            manager, planVersion: 1, planText: PlanWithEmail, Policy(off), Filter);

        var evt = manager!.Events.Single(e => e.Name == MagenticConventions.EventPlanCreated);
        evt.Tags.Should().NotContain(t => t.Key == MagenticConventions.PlanContent);
    }

    [Fact]
    public void RecordPlanCreatedWithContent_PerAttributeToggleOff_AttachesNoContent()
    {
        using var captured = new CapturedSpans();
        using var emitter = new MagenticSpanEmitter();
        var workflow = emitter.StartWorkflowSpan("wf", null, 3, null, false, ["p"]);
        var manager = emitter.StartManagerSpan(workflow);

        // Master on, but the specific plan-content toggle off → no content.
        var capture = ContentCaptureTestConfig.AllOn();
        capture.CaptureMagenticPlanContent = false;
        MagenticSpanEmitter.RecordPlanCreatedWithContent(
            manager, planVersion: 1, planText: PlanWithEmail, Policy(capture), Filter);

        var evt = manager!.Events.Single(e => e.Name == MagenticConventions.EventPlanCreated);
        evt.Tags.Should().NotContain(t => t.Key == MagenticConventions.PlanContent);
    }

    [Fact]
    public void RecordReplannedWithContent_CaptureOn_AttachesRedactedPlanContent()
    {
        using var captured = new CapturedSpans();
        using var emitter = new MagenticSpanEmitter();
        var workflow = emitter.StartWorkflowSpan("wf", null, 3, null, false, ["p"]);
        var manager = emitter.StartManagerSpan(workflow);

        MagenticSpanEmitter.RecordReplannedWithContent(
            manager, planVersion: 2, planText: PlanWithEmail,
            Policy(ContentCaptureTestConfig.AllOn()), Filter);

        var evt = manager!.Events.Single(e => e.Name == MagenticConventions.EventReplanned);
        var content = evt.Tags.Single(t => t.Key == MagenticConventions.PlanContent).Value as string;

        content!.Should().NotContain("alice@example.com");
        content.Should().Contain("[REDACTED:Email]");
        manager.GetTagItem(MagenticConventions.PlanVersion).Should().Be(2);
    }

    [Fact]
    public void RecordResetReason_CaptureOn_AttachesRedactedReason()
    {
        using var captured = new CapturedSpans();
        using var emitter = new MagenticSpanEmitter();
        var workflow = emitter.StartWorkflowSpan("wf", null, 3, null, false, ["p"]);
        var manager = emitter.StartManagerSpan(workflow);
        var reset = emitter.StartResetSpan(manager, 1, MagenticConventions.ResetTriggerStall, true);

        MagenticSpanEmitter.RecordResetReason(
            reset, "Reset because user bob@example.com reported a stall.",
            Policy(ContentCaptureTestConfig.AllOn()), Filter);

        var reason = reset!.GetTagItem(MagenticConventions.ReplanReason) as string;
        reason.Should().NotBeNull();
        reason!.Should().NotContain("bob@example.com");
        reason.Should().Contain("[REDACTED:Email]");
    }

    [Fact]
    public void RecordResetReason_CaptureOff_AttachesNoReason()
    {
        using var captured = new CapturedSpans();
        using var emitter = new MagenticSpanEmitter();
        var workflow = emitter.StartWorkflowSpan("wf", null, 3, null, false, ["p"]);
        var manager = emitter.StartManagerSpan(workflow);
        var reset = emitter.StartResetSpan(manager, 1, MagenticConventions.ResetTriggerStall, true);

        var off = ContentCaptureTestConfig.AllOn();
        off.Enabled = false;
        MagenticSpanEmitter.RecordResetReason(
            reset, "Reset because user bob@example.com reported a stall.", Policy(off), Filter);

        reset!.GetTagItem(MagenticConventions.ReplanReason).Should().BeNull();
    }
}

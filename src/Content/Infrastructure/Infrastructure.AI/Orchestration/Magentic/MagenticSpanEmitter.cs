using System.Diagnostics;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Conventions;

namespace Infrastructure.AI.Orchestration.Magentic;

/// <summary>
/// Owns the OTel <see cref="ActivitySource"/> for Magentic orchestration and
/// maps observed MAF events to the span tree described in
/// <c>documentation/architecture/magentic-spans.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Single source per process: <see cref="MagenticConventions.ActivitySourceName"/>.
/// The Presentation layer registers this name with the OTel tracer provider.
/// </para>
/// <para>
/// Span creation honors the parent-child relationships from the schema doc:
/// the root <c>invoke_workflow</c> span is the parent of the manager
/// <c>invoke_agent</c> span; round / reset / plan-review spans nest under the
/// manager span. Each <c>Start*</c> method returns the active
/// <see cref="Activity"/> so the subscriber can attach derived attributes when
/// the matching close event arrives.
/// </para>
/// <para>
/// Content-capture attributes (<see cref="MagenticConventions.PlanContent"/> and
/// friends) are intentionally NOT emitted here — PR-11 owns the toggle. The
/// constants exist on <see cref="MagenticConventions"/> so callers know which
/// keys to use when the flag is enabled, but this class never reads or writes
/// them in PR-6.
/// </para>
/// </remarks>
public sealed class MagenticSpanEmitter : IDisposable
{
    private readonly ActivitySource _source;

    /// <summary>
    /// Creates an emitter with an <see cref="ActivitySource"/> named
    /// <see cref="MagenticConventions.ActivitySourceName"/>.
    /// </summary>
    public MagenticSpanEmitter()
    {
        _source = new ActivitySource(MagenticConventions.ActivitySourceName);
    }

    /// <summary>
    /// Starts the root <c>invoke_workflow magentic.{workflow_name}</c> span and
    /// stamps the static configuration attributes (max-rounds, participants, …).
    /// </summary>
    public Activity? StartWorkflowSpan(
        string workflowName,
        int? maxRounds,
        int maxStalls,
        int? maxResets,
        bool requirePlanSignoff,
        IReadOnlyList<string> participants)
    {
        var activity = _source.StartActivity(
            $"{MagenticConventions.SpanNameWorkflowPrefix}{workflowName}",
            ActivityKind.Internal);
        if (activity is null) return null;

        activity.SetTag(GenAiSemconvRegistry.OperationName, MagenticConventions.OperationInvokeWorkflow);
        activity.SetTag(MagenticConventions.WorkflowName, workflowName);
        if (maxRounds.HasValue) activity.SetTag(MagenticConventions.MaxRounds, maxRounds.Value);
        activity.SetTag(MagenticConventions.MaxStalls, maxStalls);
        if (maxResets.HasValue) activity.SetTag(MagenticConventions.MaxResets, maxResets.Value);
        activity.SetTag(MagenticConventions.RequirePlanSignoff, requirePlanSignoff);
        activity.SetTag(MagenticConventions.Participants, string.Join(',', participants));
        return activity;
    }

    /// <summary>
    /// Starts the manager <c>invoke_agent MagenticManager</c> span as a child of
    /// the root workflow span.
    /// </summary>
    public Activity? StartManagerSpan(Activity? parent)
    {
        var activity = _source.StartActivity(
            MagenticConventions.SpanNameManager,
            ActivityKind.Internal,
            parent?.Context ?? default);
        if (activity is null) return null;

        activity.SetTag(GenAiSemconvRegistry.OperationName, MagenticConventions.OperationInvokeAgent);
        activity.SetTag(GenAiSemconvRegistry.AgentName, "MagenticManager");
        activity.SetTag(MagenticConventions.Role, MagenticConventions.RoleManager);
        return activity;
    }

    /// <summary>
    /// Starts a per-round <c>magentic.round {n}</c> span as a child of the manager
    /// span. Attributes from the progress ledger are stamped on the same call so
    /// short-lived rounds emit a single, fully populated span.
    /// </summary>
    public Activity? StartRoundSpan(
        Activity? managerSpan,
        int roundNumber,
        int stallCountAfter,
        bool isRequestSatisfied,
        bool isInLoop,
        bool isProgressBeingMade,
        string? nextSpeaker)
    {
        var activity = _source.StartActivity(
            $"{MagenticConventions.SpanNameRoundPrefix}{roundNumber}",
            ActivityKind.Internal,
            managerSpan?.Context ?? default);
        if (activity is null) return null;

        activity.SetTag(MagenticConventions.RoundNumber, roundNumber);
        activity.SetTag(MagenticConventions.RoundStallCountAfter, stallCountAfter);
        activity.SetTag(MagenticConventions.ProgressIsRequestSatisfied, isRequestSatisfied);
        activity.SetTag(MagenticConventions.ProgressIsInLoop, isInLoop);
        activity.SetTag(MagenticConventions.ProgressIsProgressBeingMade, isProgressBeingMade);
        if (!string.IsNullOrEmpty(nextSpeaker))
        {
            activity.SetTag(MagenticConventions.ProgressNextSpeaker, nextSpeaker);
        }
        activity.AddEvent(new ActivityEvent(MagenticConventions.EventProgressLedgerUpdated));
        return activity;
    }

    /// <summary>
    /// Starts a <c>magentic.reset {n}</c> span as a child of the manager span.
    /// </summary>
    public Activity? StartResetSpan(
        Activity? managerSpan,
        int resetNumber,
        string trigger,
        bool wasStalled)
    {
        var activity = _source.StartActivity(
            $"{MagenticConventions.SpanNameResetPrefix}{resetNumber}",
            ActivityKind.Internal,
            managerSpan?.Context ?? default);
        if (activity is null) return null;

        activity.SetTag(MagenticConventions.ResetNumber, resetNumber);
        activity.SetTag(MagenticConventions.ResetTrigger, trigger);
        activity.SetTag(MagenticConventions.ResetWasStalled, wasStalled);
        return activity;
    }

    /// <summary>
    /// Starts a <c>magentic.plan_review</c> span covering the HITL pause.
    /// Attribute writes for outcome occur on close via
    /// <see cref="EndPlanReviewSpan"/>.
    /// </summary>
    public Activity? StartPlanReviewSpan(
        Activity? managerSpan,
        bool isStalled,
        bool hasProgressLedger)
    {
        var activity = _source.StartActivity(
            MagenticConventions.SpanNamePlanReview,
            ActivityKind.Internal,
            managerSpan?.Context ?? default);
        if (activity is null) return null;

        activity.SetTag(MagenticConventions.PlanReviewIsStalled, isStalled);
        activity.SetTag(MagenticConventions.PlanReviewHasProgressLedger, hasProgressLedger);
        activity.AddEvent(new ActivityEvent(MagenticConventions.EventPlanReviewRequested));
        return activity;
    }

    /// <summary>
    /// Closes the plan-review span with the outcome attribute and the
    /// <c>plan_review.resolved</c> event.
    /// </summary>
    public static void EndPlanReviewSpan(Activity? planReviewSpan, bool approved)
    {
        if (planReviewSpan is null) return;
        planReviewSpan.SetTag(
            MagenticConventions.PlanReviewOutcome,
            approved
                ? MagenticConventions.PlanReviewOutcomeApproved
                : MagenticConventions.PlanReviewOutcomeRevised);
        planReviewSpan.AddEvent(new ActivityEvent(MagenticConventions.EventPlanReviewResolved));
        planReviewSpan.Dispose();
    }

    /// <summary>
    /// Records a Task Ledger event on the manager span as
    /// <c>gen_ai.orchestration.magentic.plan_created</c> with the monotonic
    /// plan version.
    /// </summary>
    public static void RecordPlanCreated(Activity? managerSpan, int planVersion)
    {
        managerSpan?.SetTag(MagenticConventions.PlanVersion, planVersion);
        managerSpan?.AddEvent(new ActivityEvent(MagenticConventions.EventPlanCreated));
    }

    /// <summary>
    /// Records a replan event on the manager span as
    /// <c>gen_ai.orchestration.magentic.replanned</c> and updates the monotonic
    /// plan version.
    /// </summary>
    public static void RecordReplanned(Activity? managerSpan, int planVersion)
    {
        managerSpan?.SetTag(MagenticConventions.PlanVersion, planVersion);
        managerSpan?.AddEvent(new ActivityEvent(MagenticConventions.EventReplanned));
    }

    /// <summary>
    /// Stamps the derived counters and completion reason on the root workflow
    /// span at workflow close.
    /// </summary>
    public static void EndWorkflowSpan(
        Activity? workflowSpan,
        int roundsExecuted,
        int resetsExecuted,
        string completionReason,
        string? errorMessage)
    {
        if (workflowSpan is null) return;
        workflowSpan.SetTag(MagenticConventions.RoundsExecuted, roundsExecuted);
        workflowSpan.SetTag(MagenticConventions.ResetsExecuted, resetsExecuted);
        workflowSpan.SetTag(MagenticConventions.CompletionReason, completionReason);
        if (!string.IsNullOrEmpty(errorMessage))
        {
            workflowSpan.SetTag(GenAiSemconvRegistry.ErrorType, errorMessage);
            workflowSpan.SetStatus(ActivityStatusCode.Error, errorMessage);
        }
        workflowSpan.Dispose();
    }

    /// <summary>
    /// Conditionally attaches the <see cref="MagenticConventions.PlanContent"/>
    /// attribute to the manager span's <see cref="MagenticConventions.EventPlanCreated"/>
    /// event when the content-capture policy permits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan text is recorded as a span event tag at observation time so
    /// the captured content lands on the exact event the rest of the schema
    /// docs anchor to. When <paramref name="policy"/> reports
    /// <see cref="IContentCapturePolicy.ShouldCaptureMagenticPlanContent"/>
    /// false this method records the bare event (matching the PR-6 default)
    /// and skips the filter call entirely.
    /// </para>
    /// </remarks>
    public static void RecordPlanCreatedWithContent(
        Activity? managerSpan,
        int planVersion,
        string? planText,
        IContentCapturePolicy policy,
        IContentRedactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(filter);

        managerSpan?.SetTag(MagenticConventions.PlanVersion, planVersion);
        var evt = BuildPlanEvent(
            MagenticConventions.EventPlanCreated,
            planText,
            policy,
            filter);
        managerSpan?.AddEvent(evt);
    }

    /// <summary>
    /// Conditionally attaches the <see cref="MagenticConventions.PlanContent"/>
    /// attribute to the manager span's <see cref="MagenticConventions.EventReplanned"/>
    /// event when the content-capture policy permits.
    /// </summary>
    public static void RecordReplannedWithContent(
        Activity? managerSpan,
        int planVersion,
        string? planText,
        IContentCapturePolicy policy,
        IContentRedactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(filter);

        managerSpan?.SetTag(MagenticConventions.PlanVersion, planVersion);
        var evt = BuildPlanEvent(
            MagenticConventions.EventReplanned,
            planText,
            policy,
            filter);
        managerSpan?.AddEvent(evt);
    }

    /// <summary>
    /// Conditionally stamps the
    /// <see cref="MagenticConventions.ReplanReason"/> attribute on a reset
    /// span when the content-capture policy permits.
    /// </summary>
    public static void RecordResetReason(
        Activity? resetSpan,
        string? replanReason,
        IContentCapturePolicy policy,
        IContentRedactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(filter);
        if (resetSpan is null) return;
        if (string.IsNullOrEmpty(replanReason)) return;
        if (!policy.ShouldCaptureMagenticReplanReason()) return;

        resetSpan.SetTag(
            MagenticConventions.ReplanReason,
            filter.Redact(replanReason, policy.Categories));
    }

    /// <summary>
    /// Conditionally stamps the
    /// <see cref="MagenticConventions.ProgressInstructionOrQuestion"/>
    /// attribute on a round span when the content-capture policy permits.
    /// </summary>
    public static void RecordRoundInstruction(
        Activity? roundSpan,
        string? instructionOrQuestion,
        IContentCapturePolicy policy,
        IContentRedactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(filter);
        if (roundSpan is null) return;
        if (string.IsNullOrEmpty(instructionOrQuestion)) return;
        if (!policy.ShouldCaptureMagenticProgressContent()) return;

        roundSpan.SetTag(
            MagenticConventions.ProgressInstructionOrQuestion,
            filter.Redact(instructionOrQuestion, policy.Categories));
    }

    /// <summary>
    /// Conditionally stamps the
    /// <see cref="MagenticConventions.PlanReviewFeedback"/> attribute on a
    /// plan-review span when the content-capture policy permits.
    /// </summary>
    public static void RecordPlanReviewFeedback(
        Activity? planReviewSpan,
        string? revisionFeedback,
        IContentCapturePolicy policy,
        IContentRedactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(filter);
        if (planReviewSpan is null) return;
        if (string.IsNullOrEmpty(revisionFeedback)) return;
        if (!policy.ShouldCaptureMagenticPlanReviewFeedback()) return;

        planReviewSpan.SetTag(
            MagenticConventions.PlanReviewFeedback,
            filter.Redact(revisionFeedback, policy.Categories));
    }

    private static ActivityEvent BuildPlanEvent(
        string eventName,
        string? planText,
        IContentCapturePolicy policy,
        IContentRedactionFilter filter)
    {
        if (string.IsNullOrEmpty(planText) || !policy.ShouldCaptureMagenticPlanContent())
        {
            return new ActivityEvent(eventName);
        }

        var redacted = filter.Redact(planText, policy.Categories);
        var tags = new ActivityTagsCollection
        {
            { MagenticConventions.PlanContent, redacted },
        };
        return new ActivityEvent(eventName, tags: tags);
    }

    /// <summary>Disposes the underlying <see cref="ActivitySource"/>.</summary>
    public void Dispose() => _source.Dispose();
}

using Domain.AI.Telemetry.Redaction;

namespace Application.AI.Common.Interfaces.Telemetry;

/// <summary>
/// Reads <c>AppConfig.AI.Telemetry.ContentCapture</c> and reports which
/// content attributes the harness is currently allowed to attach to spans.
/// Span emitters call the matching <c>ShouldCapture*</c> method before
/// invoking <see cref="IContentRedactionFilter.Redact"/> and writing the
/// attribute.
/// </summary>
/// <remarks>
/// <para>
/// All <c>ShouldCapture*</c> methods MUST return <see langword="false"/>
/// when the master <c>ContentCaptureConfig.Enabled</c> flag is off,
/// regardless of any per-attribute toggle. This ensures a single boot-time
/// decision (capture on / off) cannot be undermined by a stale per-attribute
/// flag left enabled in <c>appsettings.json</c>.
/// </para>
/// <para>
/// The policy also exposes the active redaction categories so emitters can
/// pass them straight into the filter without duplicating the lookup.
/// </para>
/// </remarks>
public interface IContentCapturePolicy
{
    /// <summary>
    /// Whether content-capture is currently enabled in any form. When this
    /// returns <see langword="false"/> every <c>ShouldCapture*</c> method
    /// also returns <see langword="false"/>.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Categories the active configuration wants redacted from captured
    /// content. Emitters MUST forward this list to
    /// <see cref="IContentRedactionFilter.Redact"/>.
    /// </summary>
    IReadOnlyList<RedactionCategory> Categories { get; }

    /// <summary>
    /// Whether <c>gen_ai.input.messages</c> may be attached to a chat /
    /// agent span.
    /// </summary>
    bool ShouldCapturePromptContent();

    /// <summary>
    /// Whether <c>gen_ai.output.messages</c> may be attached to a chat /
    /// agent span.
    /// </summary>
    bool ShouldCaptureOutputContent();

    /// <summary>
    /// Whether <c>gen_ai.tool.call.arguments</c> may be attached to an
    /// <c>execute_tool</c> span.
    /// </summary>
    bool ShouldCaptureToolCallArguments();

    /// <summary>
    /// Whether <c>gen_ai.tool.call.result</c> may be attached to an
    /// <c>execute_tool</c> span.
    /// </summary>
    bool ShouldCaptureToolCallResult();

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.plan.content</c> may be
    /// attached to a Magentic <c>plan_created</c> / <c>replanned</c> span
    /// event.
    /// </summary>
    bool ShouldCaptureMagenticPlanContent();

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.replan.reason</c> may be
    /// attached to a Magentic <c>magentic.reset</c> span.
    /// </summary>
    bool ShouldCaptureMagenticReplanReason();

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.progress.instruction_or_question</c>
    /// may be attached to a Magentic <c>magentic.round</c> span.
    /// </summary>
    bool ShouldCaptureMagenticProgressContent();

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.plan_review.feedback</c> may
    /// be attached to a Magentic <c>plan_review</c> span on a revised
    /// outcome.
    /// </summary>
    bool ShouldCaptureMagenticPlanReviewFeedback();
}

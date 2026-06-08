namespace Domain.Common.Config.AI.Telemetry;

/// <summary>
/// Per-attribute toggles for OpenTelemetry GenAI content capture. OFF by
/// default — content capture is opt-in per the OTel GenAI semantic-convention
/// guidance, and the harness defers to that posture so a fresh
/// <c>appsettings.json</c> never silently surfaces prompt or tool content in
/// traces.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is off by default (<see cref="Enabled"/> is false). When the
/// master flag is off every per-attribute toggle is also treated as off — the
/// granular flags only become live once <see cref="Enabled"/> is set.
/// </para>
/// <para>
/// When <see cref="Enabled"/> is true the harness ALSO requires the
/// <c>OTEL_SEMCONV_STABILITY_OPT_IN</c> environment variable to be pinned to
/// <see cref="Domain.AI.Telemetry.Conventions.GenAiSemconvRegistry.SemconvStabilityOptInValue"/>;
/// the registered startup validator fails the host boot when the variable is
/// unset or wrong.
/// </para>
/// </remarks>
public sealed class ContentCaptureConfig
{
    /// <summary>
    /// Master toggle. When false, every <c>Capture*</c> flag is treated as
    /// false regardless of its stored value, and the redaction filter is not
    /// invoked. Default: false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.input.messages</c> may be emitted on chat / agent
    /// spans. Default: false.
    /// </summary>
    public bool CapturePromptContent { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.output.messages</c> may be emitted on chat / agent
    /// spans. Default: false.
    /// </summary>
    public bool CaptureOutputContent { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.tool.call.arguments</c> may be emitted on
    /// <c>execute_tool</c> spans. Default: false.
    /// </summary>
    public bool CaptureToolCallArguments { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.tool.call.result</c> may be emitted on
    /// <c>execute_tool</c> spans. Default: false.
    /// </summary>
    public bool CaptureToolCallResult { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.plan.content</c> may be
    /// emitted on Magentic <c>plan_created</c> / <c>replanned</c> span events.
    /// Default: false.
    /// </summary>
    public bool CaptureMagenticPlanContent { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.replan.reason</c> may be
    /// emitted on Magentic <c>magentic.reset</c> spans. Default: false.
    /// </summary>
    public bool CaptureMagenticReplanReason { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.progress.instruction_or_question</c>
    /// may be emitted on Magentic <c>magentic.round</c> spans. Default: false.
    /// </summary>
    public bool CaptureMagenticProgressContent { get; set; }

    /// <summary>
    /// Whether <c>gen_ai.orchestration.magentic.plan_review.feedback</c> may
    /// be emitted on revised Magentic <c>plan_review</c> spans. Default: false.
    /// </summary>
    public bool CaptureMagenticPlanReviewFeedback { get; set; }

    /// <summary>
    /// Names of <see cref="Domain.AI.Telemetry.Redaction.RedactionCategory"/>
    /// values the redaction filter should apply before emission. Unknown
    /// names are ignored (with a startup log warning). Default: all categories
    /// from the enum, so a consumer that flips <see cref="Enabled"/> on
    /// without thinking about categories still gets the safest posture.
    /// </summary>
    /// <remarks>
    /// Conservative-by-default: the harness errs toward over-redaction. A
    /// consumer with a known-safe content stream can shrink this list, but
    /// has to do so deliberately.
    /// </remarks>
    // Values mirror the members of Domain.AI.Telemetry.Redaction.RedactionCategory.
    // They are literals (not nameof) because the AppConfig hierarchy lives in
    // Domain.Common, which must not depend on Domain.AI. The Infrastructure
    // ContentCapturePolicy parses these strings back to the enum and logs any
    // value it does not recognise.
    public List<string> RedactionCategories { get; set; } =
    [
        "Email",
        "Phone",
        "Ssn",
        "CreditCard",
        "IpAddress",
        "AwsKey",
        "JwtToken",
        "Generic",
    ];
}

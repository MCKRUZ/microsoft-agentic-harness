namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Classification-aware data-loss gate on the agent's live tool-call path. Before a tool touches an
/// external asset, resolves the asset's Purview sensitivity and decides whether the call may proceed,
/// must be blocked, or may run with its output redacted.
/// </summary>
/// <remarks>
/// <para>
/// Complements <see cref="IToolInvocationGovernor"/> and <see cref="IProgressEvaluator"/> at the same
/// invocation chokepoint (<c>GovernedAIFunction</c>): the governor answers "may this tool run?", the
/// progress guard answers "is the agent still making progress?", and this gate answers "is the data this
/// tool touches too sensitive to expose?". Unlike those two it needs the call <em>arguments</em> (the
/// asset identity lives there, not in the tool name) and so is consulted with them.
/// </para>
/// <para>
/// Opt-in and independent of the governor's master switch: the gate is inert unless
/// <c>DataClassificationConfig.Mode</c> is not <c>Off</c>. In <c>Audit</c> it records the decision and
/// always allows; in <c>Enforce</c> it blocks on a block decision and flags a redact decision for output
/// scrubbing. It is <em>fail-closed</em> in <c>Enforce</c>: if the classification backend cannot be
/// reached for an asset that should be classified, the call is blocked rather than allowed.
/// </para>
/// </remarks>
public interface IToolClassificationGate
{
    /// <summary>
    /// Resolves the asset a tool call targets, classifies it, and returns the gate's verdict.
    /// </summary>
    /// <param name="toolName">The tool the agent is invoking.</param>
    /// <param name="arguments">The tool-call arguments the model supplied.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="ClassificationVerdict.Allow"/> to proceed, <see cref="ClassificationVerdict.Block"/> to
    /// deny with a model-facing message, or <see cref="ClassificationVerdict.RedactOutput"/> to proceed but
    /// scrub the result via <see cref="RedactResult(string, object?)"/>. Always <see cref="ClassificationVerdict.Allow"/>
    /// when the gate is off or in audit mode.
    /// </returns>
    ValueTask<ClassificationVerdict> EvaluateAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Redacts a tool result whose asset resolved to a redact decision, by routing the result through the
    /// response-sanitizer chain. Only called by the chokepoint when a verdict's outcome is
    /// <see cref="ClassificationGateOutcome.RedactOutput"/>.
    /// </summary>
    /// <param name="toolName">The tool whose result is being redacted (passed to the sanitizers as context).</param>
    /// <param name="result">The tool's raw result object.</param>
    /// <returns>
    /// The redacted result. A text result — a raw string, a serialized JSON string, or (for an MCP tool)
    /// a <c>TextContent</c>/<c>AIContent[]</c> — is scrubbed and returned in its original shape; a
    /// structured result (JSON object/array, or any other type) is returned unchanged, since the
    /// sanitizers operate on free text and rewriting a structured value's raw text risks malforming it.
    /// Redaction therefore never alters a structured result's shape; use a block policy where structured
    /// data must not reach the model. See <c>ToolResultText</c>, which this method delegates the
    /// shape-preserving scrub to, for the exact set of shapes recognized as text.
    /// </returns>
    object? RedactResult(string toolName, object? result);

    /// <summary>
    /// String-typed overload of <see cref="RedactResult(string, object?)"/> for a caller that already
    /// knows its content is plain text and needs to leave as text — the
    /// <c>ToolCallAdmissionPipeline.TryApplyTextOutputPolicy</c> boundary (the plan step executor and
    /// the Execution API, neither of which replays structured content back to a model).
    /// </summary>
    /// <param name="toolName">The tool whose result is being redacted (passed to the sanitizers as context).</param>
    /// <param name="content">The tool's raw text.</param>
    /// <returns>
    /// The redacted text. <strong>Contract: non-null <paramref name="content"/> must produce a non-null
    /// result.</strong> <see cref="RedactResult(string, object?)"/>'s general <c>object?</c> signature
    /// legitimately returns a structured, non-string value unchanged — but a string in, string out
    /// call site has no structured shape to preserve, so a non-null-input/null-output response here can
    /// only mean this implementation broke that contract, and the caller
    /// (<c>ToolCallAdmissionPipeline</c>) treats it as one: it withholds the result and fails the call
    /// closed rather than falling back to the untreated original. This is the fix for the third instance
    /// of the <c>object?</c>-conflation defect shape this repo's CLAUDE.md already tracks twice (#490) —
    /// the return type states the guarantee instead of a caller re-deriving it from a runtime check.
    /// </returns>
    string? RedactResult(string toolName, string? content);
}

/// <summary>The action the classification gate takes for a tool call.</summary>
public enum ClassificationGateOutcome
{
    /// <summary>The call proceeds unchanged.</summary>
    Allow,

    /// <summary>The call is denied; the model receives <see cref="ClassificationVerdict.BlockedMessage"/>.</summary>
    Block,

    /// <summary>The call proceeds, but its result is scrubbed via <see cref="IToolClassificationGate.RedactResult(string, object?)"/>.</summary>
    RedactOutput
}

/// <summary>
/// The result of evaluating a tool call against the classification policy.
/// </summary>
/// <param name="Outcome">What the gate decided.</param>
/// <param name="BlockedMessage">
/// When <see cref="Outcome"/> is <see cref="ClassificationGateOutcome.Block"/>, the message returned to the
/// model in place of the tool result; null otherwise.
/// </param>
public sealed record ClassificationVerdict(ClassificationGateOutcome Outcome, string? BlockedMessage = null)
{
    // The allow verdict is immutable and consumed by value, so a single shared instance avoids a
    // per-tool-call allocation on the common (off / audit / allowed) path.
    private static readonly ClassificationVerdict AllowVerdict = new(ClassificationGateOutcome.Allow);

    // Redact carries no message, so it too can be a shared singleton.
    private static readonly ClassificationVerdict RedactVerdict = new(ClassificationGateOutcome.RedactOutput);

    /// <summary>A verdict allowing the call to proceed unchanged.</summary>
    public static ClassificationVerdict Allow() => AllowVerdict;

    /// <summary>A verdict denying the call, carrying the model-facing explanation.</summary>
    public static ClassificationVerdict Block(string blockedMessage) =>
        new(ClassificationGateOutcome.Block, blockedMessage);

    /// <summary>A verdict allowing the call but marking its result for output redaction.</summary>
    public static ClassificationVerdict RedactOutput() => RedactVerdict;
}

using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Prepares a tool's raw failure text for every consumer downstream of admission reporting — the audit
/// trail, the failure memory replayed to a human approver, and the AG-UI stream — and bounds the result
/// so an unbounded tool failure cannot make <c>EscalationExecutionRecord.FailureReason</c> pay for an
/// unbounded persisted record.
/// </summary>
/// <remarks>
/// <para>
/// Called once, from <see cref="Services.Governance.ToolCallAdmissionPipeline.ReportExecutionAsync"/> —
/// the single chokepoint every reporting path (the agent turn via <see cref="GovernedAIFunction"/>,
/// direct invocation via <see cref="DirectToolInvoker"/>) already funnels through. Before #460 this
/// treatment was hand-copied into both of those classes, redact-then-cap only, with no sanitization —
/// an MCP server (or any other tool source this process does not control) could put arbitrary,
/// unsanitized text directly in front of a human approver or into the failure memory replayed on a
/// retry. Centralizing it here means a future reporting path gets the same treatment automatically,
/// rather than needing to remember to copy it a third time.
/// </para>
/// </remarks>
internal static class ReportedFailureText
{
    private const int MaxLength = 4096;

    /// <summary>
    /// Substituted when sanitization removes all content from a failure message. A hostile string
    /// engineered to sanitize down to nothing must not silently drop the audit record and the approver
    /// notification: <c>EscalationExecutionRecord.Failed</c> and
    /// <c>InProcessApprovalFailureMemory.RecordFailure</c> both reject a null-or-whitespace failure
    /// reason, and letting that exception surface loses both the audit write and the approver
    /// notification inside <c>DefaultApprovalExecutionReporter</c>'s catch-all.
    /// </summary>
    private const string EmptyAfterSanitizationPlaceholder =
        "[tool failure text withheld: sanitization removed all content]";

    /// <summary>
    /// Substituted instead of running the full sanitize/redact pass over an implausibly large failure
    /// message. Bounds worst-case regex-scan cost on a remotely-triggered, attacker-controlled string
    /// before any of the several dozen patterns in the sanitizer/redaction chain run.
    /// </summary>
    private const int MaxScanLength = 64 * 1024;

    private static readonly string OversizedInputPlaceholder =
        $"[tool failure text withheld: exceeded {MaxScanLength} characters]";

    /// <summary>
    /// Runs <paramref name="text"/> through sanitize → redact → cap, in that order, and returns the
    /// result — safe to persist to the audit trail, replay to a human approver, or hand back to the
    /// model on a retry.
    /// </summary>
    /// <param name="text">The tool's raw, untreated failure text.</param>
    /// <param name="sanitizer">
    /// The general-purpose content sanitizer — strips injection payloads, invisible/zero-width
    /// characters, and exfiltration URLs. The same treatment <see cref="DirectToolInvoker"/>'s
    /// caller-facing copy already gets unconditionally.
    /// </param>
    /// <param name="redactionFilter">Scrubs known secret patterns (emails, SSNs, AWS keys, JWTs, etc.).</param>
    /// <param name="toolName">The tool that produced <paramref name="text"/>, passed to the sanitizer as context.</param>
    /// <returns>
    /// #472: a <see cref="PreparedFailureText"/> rather than a bare string, so "this is one of the
    /// placeholder cases below, not the tool's own message" is a field a caller checks
    /// (<see cref="PreparedFailureText.WasWithheld"/>) instead of an implicit convention that the text
    /// happens to string-equal one of three fixed constants. No caller branches on it today — the point
    /// is that a future one, or a future edit that makes a placeholder templated (e.g. including the
    /// tool name), has a type-checked case to handle rather than a string comparison to remember.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Sanitize before redact.</strong> Sanitizing first means an injection payload is stripped
    /// before the text is ever persisted or redacted, and it means redaction runs against the sanitizer's
    /// output rather than the other way round — redacting first would hand the sanitizer already-inert
    /// <c>[REDACTED:...]</c> placeholders to scan, which helps nothing. This is <em>not</em> a defense
    /// against a secret split by invisible/zero-width characters: the sanitizer's injection scrubber
    /// substitutes a visible marker for those characters rather than removing them, so a split secret
    /// currently survives (unchanged from before this method existed) — tracked separately, this ordering
    /// does not close that gap on its own.
    /// </para>
    /// <para>
    /// <strong>Cap last.</strong> Capping first can slice a real secret in half at the length boundary,
    /// and the truncated fragment that survives is never run back through the redaction filter, so it
    /// would reach the audit trail as-is. Redacting the full, uncapped text first and bounding the
    /// (already-safe) result afterward is the only ordering that can't leak a fragment.
    /// </para>
    /// </remarks>
    public static PreparedFailureText PrepareForReporting(
        string text, ICompositeResponseSanitizer sanitizer, IContentRedactionFilter redactionFilter, string? toolName)
    {
        // Bounds the cost of every pattern in the sanitizer/redaction chain before any of them run,
        // rather than only after — the 4096-char Cap() below still applies to the result, but that cap
        // does nothing to bound how much text the dozens of regex passes upstream of it must scan.
        if (text.Length > MaxScanLength)
        {
            return PreparedFailureText.Withheld(OversizedInputPlaceholder);
        }

        // Sanitize-then-redact ordering lives once in SanitizeThenRedact (#470) — this method no
        // longer needs to know the empty-after-sanitization check requires the sanitize half to run
        // first, only that it must, so the two steps stay split rather than folded into one call: the
        // withheld-placeholder decision below depends on the sanitizer's output alone, before redaction
        // ever runs.
        var sanitized = sanitizer.Sanitize(text, toolName).SanitizedContent;
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return PreparedFailureText.Withheld(EmptyAfterSanitizationPlaceholder);
        }

        var redacted = redactionFilter.Redact(sanitized, RedactionCategories.All);
        return PreparedFailureText.Reported(Cap(redacted));
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to a bounded length, if it exceeds one, via the one
    /// cut-and-mark primitive every trust-boundary truncation site shares (#467/#470).
    /// </summary>
    public static string Cap(string text) =>
        Services.Governance.BoundedText.Cap(text, MaxLength, "…[truncated]").Text;
}

/// <summary>
/// The result of <see cref="ReportedFailureText.PrepareForReporting"/> (#472): the text to report,
/// plus a type-checked flag for whether it is the tool's own (sanitized/redacted/capped) message or one
/// of the fixed withheld-placeholder cases.
/// </summary>
/// <remarks>
/// Both cases carry a non-null, non-whitespace <see cref="Text"/> — <c>EscalationExecutionRecord.Failed</c>
/// and <c>InProcessApprovalFailureMemory.RecordFailure</c> both reject one, so a withheld placeholder must
/// remain a usable string for whichever downstream consumer receives it. <see cref="WasWithheld"/> is what
/// lets a consumer tell the two apart without comparing <see cref="Text"/> against a magic constant.
/// </remarks>
/// <param name="Text">The text to report — either the tool's own prepared message, or a withheld placeholder.</param>
/// <param name="WasWithheld">
/// <see langword="true"/> when <see cref="Text"/> is one of the fixed placeholder cases (oversized input,
/// empty after sanitization, or — via <c>ToolCallAdmissionPipeline.SafePrepareFailureText</c>'s
/// catch — a sanitize/redact failure), rather than the tool's own text.
/// </param>
internal readonly record struct PreparedFailureText(string Text, bool WasWithheld)
{
    /// <summary>The tool's own text, sanitized, redacted, and capped — safe to report as-is.</summary>
    public static PreparedFailureText Reported(string text) => new(text, WasWithheld: false);

    /// <summary>One of the fixed placeholder cases, in place of the tool's own text.</summary>
    public static PreparedFailureText Withheld(string placeholder) => new(placeholder, WasWithheld: true);
}

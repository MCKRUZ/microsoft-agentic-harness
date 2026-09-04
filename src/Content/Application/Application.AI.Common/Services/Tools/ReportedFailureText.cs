using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Domain.AI.Escalation;
using Domain.AI.Telemetry.Redaction;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// <see cref="ReportedFailureText.PrepareForReporting"/>'s result: the text to report, plus why it is a
/// substitute when it is one. See <see cref="Domain.AI.Escalation.FailureTextSubstitution"/> for why the
/// discriminator itself lives in Domain rather than here.
/// </summary>
/// <param name="Text">
/// Always safe to persist to the audit trail, replay to a human approver, or hand back to the model on
/// a retry. Never null-or-whitespace: <c>EscalationExecutionRecord.Failed</c> and
/// <c>InProcessApprovalFailureMemory.RecordFailure</c> both reject that, so every substitution below is
/// itself a non-blank placeholder string — <see cref="Substitution"/> is what lets a caller tell a
/// placeholder apart from the tool's own text without relying on that string's exact wording.
/// </param>
/// <param name="Substitution">
/// <see cref="FailureTextSubstitution.None"/> when <paramref name="Text"/> is the tool's own message.
/// </param>
public readonly record struct PreparedFailureText(string Text, FailureTextSubstitution Substitution);

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
    /// The text to report — either the tool's own sanitized/redacted/capped message
    /// (<see cref="FailureTextSubstitution.None"/>), or a fixed withheld placeholder identified by its
    /// <see cref="PreparedFailureText.Substitution"/> (#472: previously an in-band sentinel string with
    /// no type-level way to tell it apart from genuine tool text — see this repo's own recorded history
    /// of that shape, e.g. <c>IacSandboxRunner</c>'s <c>ExitCode</c>/<c>Attestation</c> discriminators).
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
    /// <strong>Cap last, and every path routes through it.</strong> Capping first can slice a real secret
    /// in half at the length boundary, and the truncated fragment that survives is never run back through
    /// the redaction filter, so it would reach the audit trail as-is. Redacting the full, uncapped text
    /// first and bounding the (already-safe) result afterward is the only ordering that can't leak a
    /// fragment. Both placeholders below are far under <see cref="MaxLength"/> so routing them through
    /// <see cref="Cap"/> changes nothing observable today — but every terminal value returning from a
    /// single point is what makes "does this bypass the cap" a question with one obviously-correct
    /// answer, rather than something the next placeholder added here has to re-derive.
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
            return new PreparedFailureText(Cap(OversizedInputPlaceholder), FailureTextSubstitution.Oversized);
        }

        // Sanitize-then-redact ordering lives once in SanitizeThenRedact (#470); the withheld-placeholder
        // decision plugs into its onSanitizedEmpty hook, so this method no longer hand-rolls the same
        // sanitize → check → redact sequence every other call site already converged on.
        var sanitizedToEmpty = false;
        var reported = SanitizeThenRedact.Apply(
            text, sanitizer, redactionFilter, RedactionCategories.All, toolName,
            onSanitizedEmpty: _ =>
            {
                sanitizedToEmpty = true;
                return EmptyAfterSanitizationPlaceholder;
            });

        return new PreparedFailureText(
            Cap(reported),
            sanitizedToEmpty ? FailureTextSubstitution.SanitizedToEmpty : FailureTextSubstitution.None);
    }

    private static string Cap(string text) =>
        BoundedText.Cap(text, MaxLength, "…[truncated]").Text;
}

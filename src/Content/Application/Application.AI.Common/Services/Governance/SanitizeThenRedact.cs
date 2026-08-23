using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// The one "scrub this before it crosses a trust boundary" ordering every redact-before-persist or
/// redact-before-trace call site shares (#470): sanitize, then redact.
/// </summary>
/// <remarks>
/// <strong>Sanitize before redact, always.</strong> Sanitizing first strips injection payloads before the
/// text is ever persisted, traced, or redacted, and it means redaction runs against the sanitizer's
/// output rather than the other way round — redacting first would hand the sanitizer already-inert
/// <c>[REDACTED:...]</c> placeholders to scan, which helps nothing. <c>Tools.ReportedFailureText.PrepareForReporting</c>
/// established this ordering for the tool-failure-reporting path; <see cref="OpenTelemetry.Processors.AgentFrameworkSpanProcessor"/>
/// and <c>Infrastructure.AI.Orchestration.Magentic.MagenticSpanEmitter</c> redacted without sanitizing
/// first until #470 — an attacker splitting a secret with invisible/zero-width characters (which the
/// sanitizer's injection scrubber canonicalizes away, but the redaction filter's anchored patterns do
/// not) could defeat redaction on those two paths while the identical string was caught on the
/// tool-failure-reporting path.
/// </remarks>
public static class SanitizeThenRedact
{
    /// <summary>
    /// Bounds worst-case regex-scan cost on a remotely-triggered, attacker-controlled string before any
    /// pattern in the sanitizer/redaction chain runs — the same ceiling and rationale
    /// <c>Tools.ReportedFailureText.MaxScanLength</c> already established for the tool-failure-reporting
    /// path. Independent security review found the three call sites that route through this shared
    /// combinator (span/log content) had no such bound of their own, unlike that path.
    /// </summary>
    public const int MaxScanLength = 64 * 1024;

    /// <summary>Sanitizes <paramref name="text"/>, then redacts the sanitizer's output.</summary>
    /// <param name="text">The raw, untreated text. Cut to <see cref="MaxScanLength"/> before any pattern runs.</param>
    /// <param name="sanitizer">Strips injection payloads, invisible/zero-width characters, and exfiltration URLs.</param>
    /// <param name="redactionFilter">Scrubs known secret patterns (emails, SSNs, AWS keys, JWTs, etc.).</param>
    /// <param name="categories">Which redaction categories to scrub for.</param>
    /// <param name="context">Passed to the sanitizer as context (e.g. a tool or span name).</param>
    /// <param name="onSanitizedEmpty">
    /// Called with the sanitizer's output instead of redacting, when that output is null or whitespace —
    /// for a caller (<c>Tools.ReportedFailureText.PrepareForReporting</c>) that needs to substitute a
    /// placeholder rather than hand an empty string to a downstream consumer that rejects one. Omitted
    /// by callers with nothing that cares about the distinction: <see cref="IContentRedactionFilter.Redact"/>
    /// already treats a null/empty input as a no-op, so the ordering stays correct either way.
    /// </param>
    /// <returns>The sanitized-then-redacted text, or <paramref name="onSanitizedEmpty"/>'s result.</returns>
    public static string Apply(
        string text,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        IReadOnlyList<RedactionCategory> categories,
        string? context = null,
        Func<string, string>? onSanitizedEmpty = null)
    {
        // BoundedText.Cap, not a raw slice: guards the surrogate boundary the same way every other
        // trust-boundary truncation site does.
        var bounded = BoundedText.Cap(text, MaxScanLength, string.Empty).Text;

        var sanitized = sanitizer.Sanitize(bounded, context).SanitizedContent;
        if (onSanitizedEmpty is not null && string.IsNullOrWhiteSpace(sanitized))
            return onSanitizedEmpty(sanitized ?? string.Empty);

        return redactionFilter.Redact(sanitized, categories);
    }
}

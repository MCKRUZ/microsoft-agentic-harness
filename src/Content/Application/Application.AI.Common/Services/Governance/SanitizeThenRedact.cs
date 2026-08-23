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
    /// <summary>Sanitizes <paramref name="text"/>, then redacts the sanitizer's output.</summary>
    /// <param name="text">The raw, untreated text.</param>
    /// <param name="sanitizer">Strips injection payloads, invisible/zero-width characters, and exfiltration URLs.</param>
    /// <param name="redactionFilter">Scrubs known secret patterns (emails, SSNs, AWS keys, JWTs, etc.).</param>
    /// <param name="categories">Which redaction categories to scrub for.</param>
    /// <param name="context">Passed to the sanitizer as context (e.g. a tool or span name).</param>
    /// <returns>The sanitized-then-redacted text.</returns>
    public static string Apply(
        string text,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        IReadOnlyList<RedactionCategory> categories,
        string? context = null)
    {
        var sanitized = sanitizer.Sanitize(text, context).SanitizedContent;
        return redactionFilter.Redact(sanitized, categories);
    }
}

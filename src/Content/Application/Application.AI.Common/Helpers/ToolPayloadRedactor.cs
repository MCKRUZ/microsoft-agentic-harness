using Application.AI.Common.Interfaces;

namespace Application.AI.Common.Helpers;

/// <summary>
/// Applies the same redact-then-truncate treatment to a tool call's arguments or result before it
/// leaves the trust boundary — whether that boundary is the persisted observability store
/// (<c>ToolDiagnosticsMiddleware</c>) or a live SSE stream to a browser
/// (<c>ExecuteAgentTurnCommandHandler</c>). Tool payloads routinely carry paths, connection strings,
/// or tokens, so every exposure point needs the identical treatment — this is that one place.
/// </summary>
public static class ToolPayloadRedactor
{
    /// <summary>The length a redacted payload is capped to before it is stored or streamed.</summary>
    public const int MaxPayloadSummaryLength = 500;

    /// <summary>
    /// Redacts <paramref name="payload"/> via <paramref name="redactor"/> (a no-op when
    /// <paramref name="redactor"/> is <see langword="null"/>), then truncates the result to
    /// <paramref name="maxLength"/> characters. Only safe for free-text previews (a log line, a
    /// truncated result string a human reads) — never for a payload a consumer will parse as
    /// structured data, since truncation can land mid-token and produce invalid output. Use
    /// <see cref="Redact"/> for those.
    /// </summary>
    public static string RedactAndTruncate(string payload, ISecretRedactor? redactor, int maxLength = MaxPayloadSummaryLength)
    {
        var redacted = Redact(payload, redactor);
        return redacted.Length > maxLength ? redacted[..maxLength] : redacted;
    }

    /// <summary>
    /// Redacts <paramref name="payload"/> via <paramref name="redactor"/> (a no-op when
    /// <paramref name="redactor"/> is <see langword="null"/>) with no length cap — for a payload whose
    /// consumer needs it intact, e.g. tool-call arguments a client parses as JSON, where truncation
    /// would silently hand back invalid data instead of what was documented as the complete payload.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="redactor"/> is non-null but returned <see langword="null"/>.
    /// <see cref="ISecretRedactor.Redact"/> documents that as valid only for a null input, and
    /// <paramref name="payload"/> here is non-nullable — so it can only mean the redactor is violating
    /// its own contract. Falling back to the raw payload in that case (as a bare <c>??</c> would) is a
    /// silent unredacted leak past a redaction boundary; fail loudly instead.
    /// </exception>
    public static string Redact(string payload, ISecretRedactor? redactor)
    {
        if (redactor is null)
            return payload;

        return redactor.Redact(payload) ?? throw new InvalidOperationException(
            $"{redactor.GetType().Name}.Redact(string) returned null for non-null input, violating " +
            $"{nameof(ISecretRedactor)}'s contract.");
    }
}

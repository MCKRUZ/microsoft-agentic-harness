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
    public static string Redact(string payload, ISecretRedactor? redactor) => redactor?.Redact(payload) ?? payload;
}

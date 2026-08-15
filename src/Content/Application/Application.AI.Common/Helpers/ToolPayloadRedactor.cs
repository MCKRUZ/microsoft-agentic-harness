using Application.AI.Common.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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
    /// The length, in UTF-16 characters of the serialized (pre-redaction) JSON, above which a
    /// streamed tool call's arguments are withheld rather than sent. Unlike
    /// <see cref="MaxPayloadSummaryLength"/>, arguments are never truncated — truncating mid-JSON
    /// would hand a client invalid, unparseable data (see <see cref="Redact"/>'s remarks) — so
    /// oversized arguments are withheld whole instead of cut. Checked against the pre-redaction
    /// length deliberately: it is a resource ceiling, not a secrecy decision, so there is no reason
    /// to pay for redaction on a payload that is about to be discarded. Sits comfortably below
    /// <c>PatternSecretRedactor</c>'s own structural-redaction size ceiling (64KB), so any payload
    /// that actually reaches a client has had the full structural JSON-aware redaction pass applied
    /// to it — never just the regex-only fallback.
    /// </summary>
    public const int MaxStreamedToolCallArgsLength = 16 * 1024;

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

    /// <summary>
    /// Redacts and size-caps <paramref name="json"/> for a live tool-call-arguments stream —
    /// consolidates the identical "check the ceiling, redact, wrap the result" shape that shipped
    /// independently (and inconsistently) in both <c>ExecuteAgentTurnCommandHandler</c> (bundle SSE)
    /// and <c>AgUiClientToolBridge</c> (AG-UI client round-trip). Above
    /// <see cref="MaxStreamedToolCallArgsLength"/>, or if redaction itself throws (a redactor-contract
    /// violation), the result is withheld: <c>Json</c> is <c>"{}"</c> and <c>Withheld</c> is
    /// <see langword="true"/> — both failure modes collapse to the same client-visible signal, since
    /// either way the real arguments never reach the consumer and must not be mistaken for the tool's
    /// real (empty) input.
    /// </summary>
    /// <param name="json">The tool call's arguments, already serialized as JSON.</param>
    /// <param name="redactor">Optional secret redactor; a no-op when <see langword="null"/>.</param>
    /// <param name="logger">Logs a warning if redaction throws.</param>
    /// <param name="failureMessage">The message logged if redaction throws.</param>
    public static StreamedToolCallArguments RedactForStreaming(
        string json, ISecretRedactor? redactor, ILogger logger, string failureMessage)
    {
        if (json.Length > MaxStreamedToolCallArgsLength)
            return new StreamedToolCallArguments("{}", Withheld: true);

        try
        {
            return new StreamedToolCallArguments(Redact(json, redactor), Withheld: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{FailureMessage}", failureMessage);
            return new StreamedToolCallArguments("{}", Withheld: true);
        }
    }

    /// <summary>
    /// Guarded variant of <paramref name="produce"/> — typically a call to <see cref="Redact"/> or
    /// <see cref="RedactAndTruncate"/>, optionally preceded by payload preparation such as JSON
    /// serialization. Catches any exception (a redaction-contract violation from a misbehaving
    /// <see cref="ISecretRedactor"/>, or a serialization failure), logs it as a warning via
    /// <paramref name="logger"/>, and returns <paramref name="fallback"/> instead of propagating —
    /// so a redaction failure degrades the one payload it affects rather than aborting the caller.
    /// Centralizes the identical try/catch/log/fallback shape every exposure point around a
    /// redaction call otherwise has to hand-roll — the same "one place" reasoning behind this class
    /// applies to the failure path, not just the success path.
    /// </summary>
    /// <remarks>
    /// Logs <paramref name="failureMessage"/> as a single plain string rather than a structured
    /// template — this failure path is rare (a misbehaving redactor or an unserializable value) and
    /// shared across call sites with different natural template arguments, so a caller-formatted
    /// message is simpler than plumbing per-call structured fields through a generic helper.
    /// </remarks>
    public static string TryOrFallback(Func<string> produce, ILogger logger, string failureMessage, string fallback)
    {
        try
        {
            return produce();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{FailureMessage}", failureMessage);
            return fallback;
        }
    }

    /// <summary>
    /// Returns the text of a tool result, substituting <paramref name="genericFailureMessage"/> when
    /// <see cref="FunctionResultContent.Exception"/> is set. On failure, <see cref="FunctionResultContent.Result"/>
    /// already carries the raw exception message baked into a human-readable string —
    /// <c>FunctionInvokingChatClient</c>'s <c>IncludeDetailedErrors</c> option (set unconditionally by
    /// <c>AgentFactory</c>) appends <see cref="Exception.Message"/> verbatim, which can surface file
    /// paths, connection details, or other internals. None of <see cref="Redact"/>'s patterns are
    /// shaped to catch free-form exception prose, so redaction alone does not close this gap — every
    /// consumer of a tool result's text (a live SSE stream, or the persisted trace store a dashboard
    /// later renders) must call this before <see cref="Redact"/>/<see cref="RedactAndTruncate"/>, not
    /// just the one exposure point that happened to be reviewed first.
    /// </summary>
    public static string SafeResultText(FunctionResultContent result, string genericFailureMessage = "Error: tool call failed.") =>
        result.Exception is not null ? genericFailureMessage : result.Result?.ToString() ?? string.Empty;
}

using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Helpers;

/// <summary>
/// The "sanitize, and log a warning if anything changed" wrapper both
/// <see cref="ToolCallTranscriptExtractor"/> (#513, the replay-persistence path) and
/// <c>ExecuteAgentTurnCommandHandler</c> (#556, the live AG-UI streaming path) need around
/// <see cref="ToolCallIdentifierSanitizer"/>. Extracted after both independently carried the
/// identical shape (#556 code-review) — <see cref="ToolCallIdentifierSanitizer"/>'s own remarks
/// already name the cost of NOT sharing a primitive the first time it's needed twice.
/// </summary>
public static class ToolCallIdentifierLogging
{
    /// <summary>
    /// Sanitizes <paramref name="value"/>, logging a structured warning naming the caller-supplied
    /// <paramref name="source"/> and <paramref name="consequence"/> when anything changed.
    /// </summary>
    /// <param name="value">The raw tool CallId or tool name to sanitize.</param>
    /// <param name="logger">Logger for the warning.</param>
    /// <param name="source">
    /// The calling type's name, for the log message's <c>[Source]</c> prefix — distinguishes which
    /// pipeline stage caught the value.
    /// </param>
    /// <param name="fieldName">Either <c>"CallId"</c> or <c>"ToolName"</c>, for the log message.</param>
    /// <param name="correlationId">
    /// Never the raw, attacker-controlled value (CWE-117): a hostile CallId could carry log-forging
    /// control characters, or an unbounded payload past what <see cref="ToolCallIdentifierSanitizer.MaxLength"/>
    /// only bounds in the PERSISTED value. Pass the already-sanitized CallId when sanitizing a
    /// paired ToolName, or <see langword="null"/> when sanitizing the CallId itself — in which case
    /// the value's own cleaned replacement is the correlation id.
    /// </param>
    /// <param name="consequence">
    /// What happens to the sanitized value next, for the log message's trailing phrase — e.g.
    /// <c>"before persisting for replay"</c> or <c>"before streaming to the client"</c>.
    /// </param>
    /// <returns>
    /// The sanitized value, as a <see cref="SanitizedIdentifier"/> (#633) — never the raw
    /// <paramref name="value"/> reinterpreted as safe, so a caller cannot accidentally forward the
    /// unsanitized input to a consumer that requires an already-cleaned identifier.
    /// </returns>
    public static SanitizedIdentifier SanitizeAndLogIfChanged(
        string value, ILogger logger, string source, string fieldName, string? correlationId, string consequence)
    {
        var (result, changed) = ToolCallIdentifierSanitizer.Sanitize(value);
        if (!changed)
            return new SanitizedIdentifier(value);

        logger.LogWarning(
            "[{Source}] {Field} for CallId={CallId} was truncated or contained characters outside " +
            "the expected identifier shape ([A-Za-z0-9_-]); replaced with {Sanitized} {Consequence}.",
            source, fieldName, correlationId ?? result.ToString(), result, consequence);

        return result;
    }
}

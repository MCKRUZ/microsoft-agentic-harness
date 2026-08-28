using Application.AI.Common.Interfaces;
using Domain.Common.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Helpers;

/// <summary>
/// One tool call paired with its matching result, extracted from a turn's message sequence.
/// </summary>
/// <param name="CallId">The provider-assigned id pairing this call to its result.</param>
/// <param name="ToolName">The invoked tool's name.</param>
/// <param name="ArgsJson">
/// The call's arguments, JSON-serialized. <see langword="null"/> when the call had no arguments or
/// serialization failed.
/// </param>
/// <param name="ResultText">
/// The result's text, or <see langword="null"/> when <see cref="HasResult"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="HasResult">
/// <see langword="false"/> for an orphaned call — one with no matching
/// <see cref="FunctionResultContent"/> in the same message sequence. This genuinely happens
/// (unknown-call termination, iteration-limit exhaustion, a blocked client-side tool), and callers
/// that persist an exchange must never silently drop the distinction: a persisted call with no
/// result is a malformed conversation a provider will reject on the next turn.
/// </param>
/// <param name="RoundOrdinal">
/// This call's position among every call in the same message sequence, zero-based in the order the
/// calls appear. Preserves genuine sequencing — a turn that called A, read A's result, then called
/// B must not replay as though A and B were issued in parallel.
/// </param>
public readonly record struct ToolExchange(
    string CallId,
    string ToolName,
    string? ArgsJson,
    string? ResultText,
    bool HasResult,
    int RoundOrdinal);

/// <summary>
/// Extracts paired tool-call/tool-result exchanges from a turn's message sequence, for persisting
/// full tool-call fidelity rather than just a turn's final narrated text.
/// </summary>
/// <remarks>
/// Deliberately takes a plain message sequence, not an <see cref="AgentResponse"/> — the
/// <see cref="AgentResponse"/> overload is a one-line adapter over it. If the assumption this
/// extractor's callers build on (that tool-call/result content survives into
/// <see cref="AgentResponse.Messages"/>, proven by <c>ToolCallCaptureFeasibilityTests</c>) ever stops
/// holding, the fallback is to capture from inside the middleware pipeline instead — already proven
/// to work in this repo, and swapping the data source changes nothing downstream of this type.
/// <para>
/// Does no free-text redaction or sanitization — <see cref="ToolExchange.ArgsJson"/> and
/// <see cref="ToolExchange.ResultText"/> pass through exactly as captured, and extraction/treatment
/// of them stay separate concerns with different callers and different failure modes; see the
/// security-treatment layer this extractor's output feeds into. <see cref="ToolExchange.ToolName"/>
/// and <see cref="ToolExchange.CallId"/> are the one exception (#513): both are narrowed to an
/// identifier shape (see <see cref="SanitizeIdentifier"/>) here, at extraction, rather than treated
/// as free text downstream — a fundamentally different, narrower operation than the sanitize/redact
/// pipeline the payload fields still go through elsewhere.
/// </para>
/// </remarks>
public static class ToolCallTranscriptExtractor
{
    /// <summary>
    /// Extracts every tool-call/tool-result exchange from <paramref name="messages"/>, in the order
    /// the calls appear.
    /// </summary>
    public static IReadOnlyList<ToolExchange> Extract(
        IEnumerable<ChatMessage> messages, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(logger);

        var contents = messages.SelectMany(m => m.Contents).ToList();

        // Two passes: first establish call order and identity, then attach results by CallId. A
        // single pass can't do this correctly — a result can be interleaved with, or even precede
        // (a defensively-ordered response), the call it answers.
        //
        // Deduplicated by CallId, first occurrence wins — a CallId should be unique per turn, but a
        // provider connector surfacing the same call twice (ToolCallOrderingSink's own doc comment
        // names this as a real, guarded-against failure mode on the streaming path; that guard only
        // gates what reaches the live SSE sink, not what this extractor sees) would otherwise produce
        // two ToolExchange records sharing one CallId, which then persist as two assistant/tool
        // message pairs with the same id on replay — most providers reject that as an invalid
        // duplicate tool_call id, permanently breaking the conversation on every later turn.
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        var calls = contents.OfType<FunctionCallContent>()
            .Where(c => !string.IsNullOrEmpty(c.CallId) && !string.IsNullOrEmpty(c.Name) && seenCallIds.Add(c.CallId))
            .ToList();

        var resultsByCallId = contents.OfType<FunctionResultContent>()
            .Where(r => !string.IsNullOrEmpty(r.CallId))
            .GroupBy(r => r.CallId)
            // A CallId should be unique per turn; if a provider ever sends more than one result for
            // the same call, the last one wins rather than throwing — extraction must not fail a
            // whole turn's persistence over one malformed provider response.
            .ToDictionary(g => g.Key, g => g.Last());

        var exchanges = new List<ToolExchange>(calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var argsJson = TrySerializeArguments(call, logger);

            // Pairing above (calls, resultsByCallId) is keyed on the RAW call.CallId — sanitizing
            // before the lookup would risk two distinct raw ids collapsing to the same sanitized
            // value and silently mismatching a call to the wrong result. Only the id and name
            // actually placed into the persisted record are sanitized, below. correlationId is
            // always an already-sanitized value (or null, self-correlating) — never the raw,
            // attacker-controlled call.CallId — so a hostile CallId can never reach a log sink
            // unsanitized via the warning below, only its cleaned replacement.
            var safeCallId = SanitizeIdentifier(call.CallId, logger, "CallId", correlationId: null);
            var safeName = SanitizeIdentifier(call.Name, logger, "ToolName", correlationId: safeCallId);

            if (resultsByCallId.TryGetValue(call.CallId, out var result))
            {
                exchanges.Add(new ToolExchange(
                    safeCallId, safeName, argsJson, ResultText(result), HasResult: true, RoundOrdinal: i));
            }
            else
            {
                exchanges.Add(new ToolExchange(
                    safeCallId, safeName, argsJson, ResultText: null, HasResult: false, RoundOrdinal: i));
            }
        }

        return exchanges;
    }

    /// <summary>
    /// The longest a tool name or call id may be once persisted for replay (#513). Well above every
    /// provider's own limit, so a legitimate value is never truncated — a value that reaches this
    /// ceiling is already suspicious on length alone, independent of what characters it contains.
    /// </summary>
    internal const int MaxIdentifierLength = 128;

    /// <summary>Hex characters of the collision-guard hash suffix — mirrors <c>BundleOwnedMcpToolNaming</c>'s own 5-byte suffix.</summary>
    private const int HashSuffixHexLength = 10;

    private const string HashSuffixSeparator = "_";

    /// <summary>
    /// Narrows a tool name or call id to the shape both are supposed to have, before either is ever
    /// persisted for replay (#513) — previously reaching the conversation store, and the model's own
    /// context on every later turn, with no character-class restriction at all, unlike the free-text
    /// payload beside it that already goes through <see cref="IToolCallReplayTreatment.Treat"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="IToolCallReplayTreatment.Treat"/> — that pass is built for free
    /// text (sanitize an injection payload, then redact secret patterns, then size-tier), and a tool
    /// name or call id has a much narrower legitimate shape than a payload. Matching
    /// <c>BundleOwnedMcpToolNaming.Sanitize</c>'s own precedent for the identical question ("what
    /// characters can a tool name actually contain"), an allowlist of ASCII letters, digits,
    /// underscore, and hyphen is both stricter — nothing outside that set survives, so there is no
    /// character class left for an injection payload to exploit — and cheaper than running the full
    /// treatment pipeline on a string that was never meant to carry free text in the first place. The
    /// extractor does not verify the name resolves to a declared tool (a hallucinated or
    /// attacker-suggested name still produces a <see cref="ToolExchange"/>), so this is the only gate
    /// between a provider- or model-supplied identifier and durable, model-facing persistence.
    /// <para>
    /// Also matches <c>BundleOwnedMcpToolNaming.SanitizeWithCollisionGuard</c>'s own precedent for
    /// avoiding what that method's doc comment calls out directly: mapping every disallowed character
    /// to the same <c>'_'</c> collapses information, so two distinct raw values (e.g. <c>"call#1"</c>
    /// and <c>"call$1"</c>) can sanitize to an identical string. This type's own dedup (by raw,
    /// pre-sanitization CallId) runs before either value is sanitized, so both would otherwise survive as separate
    /// <see cref="ToolExchange"/> records sharing one persisted CallId — the same "duplicate
    /// tool_call id" hazard the raw-duplicate dedup exists to prevent, reintroduced by sanitization
    /// itself. A hash suffix of the original raw value, appended only when sanitization actually
    /// changed something, keeps distinct raw values distinct after sanitizing.
    /// </para>
    /// </remarks>
    private static string SanitizeIdentifier(string value, ILogger logger, string fieldName, string? correlationId)
    {
        var truncated = value.Length > MaxIdentifierLength ? value[..MaxIdentifierLength] : value;
        var changed = truncated.Length != value.Length;

        var chars = new char[truncated.Length];
        for (var i = 0; i < truncated.Length; i++)
        {
            var c = truncated[i];
            var safe = char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
            chars[i] = safe ? c : '_';
            changed |= !safe;
        }

        // Fast path: the overwhelming common case is an already-clean, already-short value — skip the
        // allocation above entirely rather than discarding it.
        if (!changed)
            return value;

        var suffix = $"{HashSuffixSeparator}{Sha256HexPrefixHelper.Compute(value, HashSuffixHexLength)}";
        var keep = Math.Max(0, MaxIdentifierLength - suffix.Length);
        var basePart = chars.Length > keep ? new string(chars, 0, keep) : new string(chars);
        var result = $"{basePart}{suffix}";

        // Never log the raw, attacker-controlled value here (CWE-117): a hostile CallId could
        // otherwise carry log-forging control characters or, since the ceiling above only bounds
        // what gets persisted, an unbounded payload straight into the log sink. correlationId is
        // always already-sanitized (or null when this call is sanitizing the CallId itself, in
        // which case the value's own cleaned replacement is the correlation id).
        logger.LogWarning(
            "[ToolCallTranscriptExtractor] {Field} for CallId={CallId} was truncated or contained " +
            "characters outside the expected identifier shape ([A-Za-z0-9_-]); replaced with " +
            "{Sanitized} before persisting for replay.",
            fieldName, correlationId ?? result, result);

        return result;
    }

    /// <summary>Adapter over <see cref="Extract(IEnumerable{ChatMessage}, ILogger)"/> for an agent's response.</summary>
    public static IReadOnlyList<ToolExchange> Extract(AgentResponse response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Extract(response.Messages, logger);
    }

    private static string? TrySerializeArguments(FunctionCallContent call, ILogger logger)
    {
        if (call.Arguments is not { Count: > 0 } args)
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Serialize(args);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[ToolCallTranscriptExtractor] Failed to serialize arguments for {Tool} CallId={CallId}",
                call.Name, call.CallId);
            return null;
        }
    }

    /// <summary>
    /// Resolves a result's text for replay. Deliberately not <c>ToolPayloadRedactor.SafeResultText</c>
    /// — that helper calls <c>.ToString()</c> on a non-string <see cref="FunctionResultContent.Result"/>,
    /// which is correct for its own callers (an observability preview, where a CLR type name is a
    /// tolerable stand-in) but wrong here: a structured result returned as a raw object rather than
    /// pre-serialized JSON text would replay to the model as a type name instead of the payload it
    /// actually returned — silently discarding exactly the content this extractor exists to preserve.
    /// The exception-substitution policy is still reused: a failed call's raw exception text must not
    /// reach the model any more than it should reach an observability store.
    /// </summary>
    private static string? ResultText(FunctionResultContent result)
    {
        if (result.Exception is not null)
            return "Error: tool call failed.";

        return result.Result switch
        {
            null => null,
            string text => text,
            var value => TrySerializeResult(value, result.CallId),
        };
    }

    private static string? TrySerializeResult(object value, string? callId)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(value);
        }
        catch (Exception)
        {
            // No logger reaches this static branch (ResultText has none) — matches
            // TokenEstimationHelper.TrySerializeAndEstimate's precedent for the same shape: a
            // degraded result (here, a generic marker) beats throwing out of extraction for one
            // unserializable value.
            return $"[unserializable tool result for CallId={callId}]";
        }
    }
}

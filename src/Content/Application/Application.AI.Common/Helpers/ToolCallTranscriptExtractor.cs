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
/// Does no redaction or sanitization — extraction and treatment are separate concerns with different
/// callers and different failure modes; see the security-treatment layer this extractor's output
/// feeds into.
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
        var calls = contents.OfType<FunctionCallContent>()
            .Where(c => !string.IsNullOrEmpty(c.CallId) && !string.IsNullOrEmpty(c.Name))
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

            if (resultsByCallId.TryGetValue(call.CallId, out var result))
            {
                exchanges.Add(new ToolExchange(
                    call.CallId, call.Name, argsJson, ResultText(result), HasResult: true, RoundOrdinal: i));
            }
            else
            {
                exchanges.Add(new ToolExchange(
                    call.CallId, call.Name, argsJson, ResultText: null, HasResult: false, RoundOrdinal: i));
            }
        }

        return exchanges;
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

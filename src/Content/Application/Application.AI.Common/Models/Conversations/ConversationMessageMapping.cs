using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Models.Conversations;

/// <summary>
/// Projects stored transcript messages onto the shape the agent framework dispatches, so every caller
/// that replays a conversation to a model does it the same way.
/// </summary>
/// <remarks>
/// <para>
/// This mapping was written out by hand in three places — the SignalR orchestrator, the AG-UI handler,
/// and the shared multi-turn loop — each an identical <c>switch</c> over the same enum. Three copies of
/// one mapping is three chances to add a role in one of them: a role missing from a copy does not fail,
/// it silently replays as the fallback, and the only symptom is a model that was told the wrong speaker
/// said something.
/// </para>
/// <para>
/// The fallback is deliberate rather than defensive. <see cref="MessageRole"/> is closed and every
/// member is mapped, so the arm is unreachable today; it exists because a role added later must not
/// throw halfway through building a prompt, and <see cref="ChatRole.User"/> is the reading that
/// attributes an unknown speaker to the least privileged one.
/// </para>
/// </remarks>
public static class ConversationMessageMapping
{
    /// <summary>Maps a stored message role onto the agent framework's chat role.</summary>
    /// <param name="role">The stored role.</param>
    /// <remarks>
    /// Private deliberately. Every caller wants a whole window projected, not one role converted, and
    /// in a template consumers clone and extend, a public member is a supported member.
    /// </remarks>
    private static ChatRole ToChatRole(MessageRole role) => role switch
    {
        MessageRole.User => ChatRole.User,
        MessageRole.Assistant => ChatRole.Assistant,
        MessageRole.System => ChatRole.System,
        MessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };

    /// <summary>
    /// Projects a transcript window onto chat messages, oldest first, ready to seed a dispatch.
    /// </summary>
    /// <param name="messages">The window, in transcript order.</param>
    /// <param name="replayToolCalls">
    /// Whether an assistant row's <see cref="ConversationMessage.ToolCalls"/> should expand into real
    /// call/result content (see the remarks below) or be skipped in favor of the row's narrated text
    /// only. Callers must source this from <c>IToolCallReplayTreatment.Enabled</c>, not default it —
    /// an operator's kill switch for this feature (<c>AppConfig:AI:Conversations:ToolCallReplay:Enabled</c>)
    /// has to stop replaying <em>already-persisted</em> tool payloads, not just stop writing new ones:
    /// gating only the write side (as <c>ExecuteAgentTurnCommandHandler.BuildTreatedToolCallRecords</c>
    /// already does) would leave every conversation with tool history from before the flag was flipped
    /// still shipping that content to the model on every later turn. Defaults to
    /// <see langword="true"/> only so this method's own unit tests, which are about expansion
    /// correctness and not about the gate, don't have to pass it.
    /// </param>
    /// <remarks>
    /// <para>
    /// Widget messages carry empty content and are excluded upstream by
    /// <c>IConversationStore.GetHistoryForDispatch</c>, so every other row is a straight text
    /// projection — except an assistant row carrying <see cref="ConversationMessage.ToolCalls"/>,
    /// which <em>expands</em>, when <paramref name="replayToolCalls"/> is <see langword="true"/>: one
    /// assistant/tool message pair per call, oldest <see cref="ToolCallRecord.RoundOrdinal"/> first,
    /// followed by the assistant's own text (when non-empty) as a final message. This is what turns a
    /// resumed conversation's memory from narrated prose back into the real call/result pair the model
    /// produced (#249 item 6).
    /// </para>
    /// <para>
    /// One pair per record, not one per <see cref="ToolCallRecord.RoundOrdinal"/> group — matching
    /// <c>ToolCallTranscriptExtractor</c>, which assigns every call in a turn its own distinct ordinal,
    /// never a shared one. If a producer ever needs several calls that genuinely happened in parallel
    /// to replay as one assistant message carrying multiple <see cref="FunctionCallContent"/> entries
    /// (the wire shape a live parallel tool round actually takes), that grouping belongs here too —
    /// deliberately not built ahead of a case nothing today produces.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChatMessage> ToChatMessages(
        IReadOnlyList<ConversationMessage> messages, bool replayToolCalls = true)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var result = new List<ChatMessage>();

        foreach (var message in messages)
        {
            if (!replayToolCalls
                || message.Role != MessageRole.Assistant
                || message.ToolCalls is not { Count: > 0 } toolCalls)
            {
                result.Add(new ChatMessage(ToChatRole(message.Role), message.Content));
                continue;
            }

            AppendToolCallRounds(result, toolCalls);

            if (!string.IsNullOrEmpty(message.Content))
            {
                result.Add(new ChatMessage(ChatRole.Assistant, message.Content));
            }
        }

        return result;
    }

    /// <summary>
    /// Appends one assistant/tool message pair per call, oldest <see cref="ToolCallRecord.RoundOrdinal"/>
    /// first, so a resumed conversation replays tool activity in the sequence it actually happened.
    /// </summary>
    private static void AppendToolCallRounds(List<ChatMessage> result, IReadOnlyList<ToolCallRecord> toolCalls)
    {
        // Defensive sort, not a no-op: ToolCallTranscriptExtractor always builds this list in
        // ascending-ordinal order, but a persisted record read back from storage is data this code
        // doesn't control the shape of.
        var ordered = toolCalls
            .Select((call, index) => (call, ordinal: call.RoundOrdinal ?? index))
            .OrderBy(x => x.ordinal);

        foreach (var (call, _) in ordered)
        {
            // A synthesized id only ever fires for a hypothetical pre-#249-item-6 record
            // deserialized without a CallId — production writers always populate it (see
            // ToolCallTranscriptExtractor). Guid-based rather than a per-row counter so it stays
            // unique across the whole window, not just within one row.
            var callId = string.IsNullOrEmpty(call.CallId)
                ? $"replayed-{Guid.NewGuid():N}"
                : call.CallId;

            result.Add(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, call.ToolName, ReconstructArguments(call.Input))]));
            result.Add(new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(callId, call.Output)]));
        }
    }

    /// <summary>
    /// Reconstructs a call's arguments from its treated, persisted form for replay.
    /// </summary>
    /// <remarks>
    /// <see cref="ToolCallRecord.Input"/> is not guaranteed to still be valid JSON — the security
    /// treatment layer can truncate it with a visible marker or withhold it outright (see
    /// <c>IToolCallReplayTreatment</c>) — so a failed parse degrades to a single-entry map carrying the
    /// raw treated text rather than losing it or throwing out of a replay projection.
    /// <para>
    /// Deliberately not <c>ToolParameters.FromJson</c>, despite the surface similarity (both turn a
    /// JSON blob into a string-keyed map with a raw-text fallback): that helper exists specifically so
    /// an agent's tool call and a direct HTTP invocation of the same tool resolve to identical CLR
    /// argument shapes — it flattens numbers to <see langword="long"/>/<see langword="double"/> and
    /// matches keys case-insensitively, because a tool reads its arguments by type. This method instead
    /// reconstructs a <see cref="FunctionCallContent.Arguments"/> dictionary for replay, where the
    /// values a live provider response actually populates are boxed <see cref="JsonElement"/>s with
    /// ordinal (case-sensitive) keys — exactly what a raw <c>JsonSerializer.Deserialize</c> onto
    /// <see cref="Dictionary{TKey,TValue}"/> produces. Routing through <c>ToolParameters.Flatten</c>
    /// here would silently change replayed arguments' value types and key comparer from what the model
    /// actually produced, for no benefit — this method's caller never executes a tool with the result.
    /// </para>
    /// </remarks>
    private static IDictionary<string, object?>? ReconstructArguments(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(input);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?> { ["_raw"] = input };
        }
    }
}

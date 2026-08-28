using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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
    /// Core projection logic, taking the two <see cref="ToolCallReplayWindowPolicy"/> settings as loose
    /// parameters rather than the bundled record. Internal rather than private only so the test suite
    /// that exercises the budget/expansion mechanics directly (predating
    /// <see cref="ToolCallReplayWindowPolicy"/>) keeps working without every test constructing a policy
    /// record it isn't testing — every production caller goes through the public, policy-based overload
    /// below instead, which is where a caller belongs: one entry point production code can reach for,
    /// not two kept in sync only by convention.
    /// </summary>
    /// <param name="messages">The window, in transcript order.</param>
    /// <param name="replayToolCalls">
    /// Whether an assistant row's <see cref="ConversationMessage.ToolCalls"/> should expand into real
    /// call/result content (see the remarks below) or be skipped in favor of the row's narrated text
    /// only. Source it from <c>IToolCallReplayTreatment.Enabled</c> — an operator's kill switch for this
    /// feature (<c>AppConfig:AI:Conversations:ToolCallReplay:Enabled</c>) has to stop replaying
    /// <em>already-persisted</em> tool payloads, not just stop writing new ones: gating only the write
    /// side (as <c>ExecuteAgentTurnCommandHandler.BuildTreatedToolCallRecords</c> already does) would
    /// leave every conversation with tool history from before the flag was flipped still shipping that
    /// content to the model on every later turn. Deliberately has no default, for the same reason as
    /// <paramref name="maxReplayedChars"/>.
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
    /// <param name="maxReplayedChars">
    /// Total budget, in characters of treated tool-call text, for the whole window. Source it from
    /// <c>IToolCallReplayTreatment.MaxReplayedChars</c>: this is the only bound on what a resumed
    /// conversation costs per turn. The store's dispatch window is capped in <em>rows</em>, and one row
    /// expands here into two chat messages per tool call it carries, so once turns are tool-heavy that
    /// row cap stops bounding the prompt at all. Deliberately has no default — an omitted budget would
    /// mean an unbounded prompt on every resumed turn, with nothing failing to say so, and a required
    /// parameter is the only form of that rule the compiler can enforce.
    /// </param>
    /// <param name="logger">
    /// Optional, for reporting what the budget dropped. Matching <c>ToolCallTranscriptExtractor.Extract</c>,
    /// which takes a logger the same way and for the same reason — silently shrinking a model's memory
    /// is exactly the kind of change that must leave a trace.
    /// </param>
    internal static IReadOnlyList<ChatMessage> ToChatMessages(
        IReadOnlyList<ConversationMessage> messages,
        bool replayToolCalls,
        int maxReplayedChars,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Handled up front rather than as a clause inside the loop below. Keeping it here means the
        // budget array is never null, so the loop needs no null-forgiving index and no second check of
        // this same flag twelve lines apart — a coupling that was safe only by inspection.
        if (!replayToolCalls)
        {
            return [.. messages.Select(m => new ChatMessage(ToChatRole(m.Role), m.Content))];
        }

        // Computed up front, over the whole window, because the budget is a property of the window and
        // not of any one row: which of a row's calls survive depends on how much every LATER row
        // already spent, which a single forward pass cannot know when it reaches that row.
        var (admittedByRow, admittedCalls) = SelectCallsWithinBudget(messages, maxReplayedChars, logger);

        // Sized exactly: one message per row, plus the call/result pair each admitted call expands into.
        // The count is already in hand from the pass above, so this costs nothing and saves the handful
        // of doubling reallocations a tool-heavy window would otherwise walk through.
        var result = new List<ChatMessage>(messages.Count + (2 * admittedCalls));

        // Scoped to the WHOLE window, not to one row: ToolCallTranscriptExtractor dedupes call ids
        // within a turn, but nothing dedupes across turns, and some provider connectors number call
        // ids per-turn and reset (call_0, call_1, call_0 again next turn — ToolCallOrderingSink's own
        // remarks document this as real, which is why that type must be built fresh per turn). Two
        // turns can therefore each have persisted "call_0", and replaying both here would put two
        // tool_calls entries carrying one id into a single request — which providers reject. Because
        // the window is rebuilt from PERSISTED rows every turn, that rejection would recur forever,
        // with no recovery short of deleting the conversation.
        var usedCallIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            if (message.Role != MessageRole.Assistant || message.ToolCalls is not { Count: > 0 })
            {
                result.Add(new ChatMessage(ToChatRole(message.Role), message.Content));
                continue;
            }

            // May be empty when the budget dropped every one of this row's calls. The row then behaves
            // exactly like a tool-only turn whose calls were never persisted: its narrated text (if any)
            // still replays below, so the model keeps the prose account of what happened even where it
            // has lost the literal call/result pairs.
            AppendToolCallRounds(result, admittedByRow![i], usedCallIds);

            if (!string.IsNullOrEmpty(message.Content))
            {
                result.Add(new ChatMessage(ChatRole.Assistant, message.Content));
            }
        }

        return result;
    }

    /// <summary>
    /// Projects a transcript window onto chat messages, oldest first, ready to seed a dispatch — the
    /// public entry point every production caller uses. See the internal
    /// <see cref="ToChatMessages(IReadOnlyList{ConversationMessage},bool,int,ILogger?)"/> overload's
    /// own remarks for the full expansion/budget mechanics this delegates to.
    /// </summary>
    /// <param name="messages">The window, in transcript order.</param>
    /// <param name="replayPolicy">
    /// Whether tool calls expand at all, and the character budget for the whole window if they do — see
    /// <see cref="ToolCallReplayWindowPolicy"/>'s own remarks for what each setting means and for why
    /// bundling them, and being explicit about when they're read, exists at all. Build via
    /// <see cref="ToolCallReplayWindowPolicy.FromCurrentSettings"/>.
    /// </param>
    /// <param name="logger">
    /// Optional, for reporting what the budget dropped. Matching <c>ToolCallTranscriptExtractor.Extract</c>,
    /// which takes a logger the same way and for the same reason — silently shrinking a model's memory
    /// is exactly the kind of change that must leave a trace.
    /// </param>
    public static IReadOnlyList<ChatMessage> ToChatMessages(
        IReadOnlyList<ConversationMessage> messages,
        ToolCallReplayWindowPolicy replayPolicy,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(replayPolicy);
        return ToChatMessages(messages, replayPolicy.ReplayToolCalls, replayPolicy.MaxReplayedChars, logger);
    }

    /// <summary>
    /// Decides, for every row in the window, which of its tool calls fit inside
    /// <paramref name="maxReplayedChars"/> — newest first, dropping the oldest until the remainder
    /// fits — and returns them per row in ascending <see cref="ToolCallRecord.RoundOrdinal"/> order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newest-first is the admission order because a replayed window is context, not an audit log: the
    /// tool activity the current turn is most likely to reason about is the most recent. Admission
    /// <em>latches</em> shut at the first call that does not fit rather than continuing to look for a
    /// smaller one further back — that keeps the surviving set a contiguous newest tail, where
    /// skip-and-continue would punch holes through the middle of the history and replay a sequence
    /// that never happened.
    /// </para>
    /// <para>
    /// A budget smaller than the single newest call admits nothing. That is the honest reading of a
    /// ceiling rather than an oversight: the alternative — always keeping one call — would mean the
    /// bound can be exceeded by an unbounded amount, which is not a bound. The rows' own text still
    /// replays either way.
    /// </para>
    /// <para>
    /// Cost counts every part of a record that actually reaches the model:
    /// <see cref="ToolCallRecord.Input"/>, <see cref="ToolCallRecord.Output"/>,
    /// <see cref="ToolCallRecord.ToolName"/> and <see cref="ToolCallRecord.CallId"/>. The last two are
    /// normally tens of characters and were originally left out on those grounds — but both are
    /// model-supplied strings that no treatment pass bounds, so "normally small" is an assumption about
    /// untrusted input rather than a property of it. Counting them costs one addition and removes a way
    /// for a budget to be quietly exceeded.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<ToolCallRecord>[] AdmittedByRow, int AdmittedCalls) SelectCallsWithinBudget(
        IReadOnlyList<ConversationMessage> messages,
        int maxReplayedChars,
        ILogger? logger)
    {
        var admittedByRow = new IReadOnlyList<ToolCallRecord>[messages.Count];
        var spent = 0;
        var dropped = 0;
        var admittedCalls = 0;
        var budgetExhausted = false;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role != MessageRole.Assistant || message.ToolCalls is not { Count: > 0 } calls)
            {
                admittedByRow[i] = [];
                continue;
            }

            // Once the budget has latched shut, every older row is dropped wholesale — no sort, no
            // per-call arithmetic, since nothing behind the latch can be admitted by definition.
            if (budgetExhausted)
            {
                dropped += calls.Count;
                admittedByRow[i] = [];
                continue;
            }

            // Defensive sort, not a no-op: ToolCallTranscriptExtractor always builds this list in
            // ascending-ordinal order, but a persisted record read back from storage is data this code
            // doesn't control the shape of. Done here rather than at append time so the budget walks
            // the calls in the same order the model will see them.
            var ordered = calls
                .Select((call, index) => (call, ordinal: call.RoundOrdinal ?? index))
                .OrderBy(x => x.ordinal)
                .Select(x => x.call)
                .ToList();

            // What survives is a suffix of `ordered`, so the result is one index rather than a second
            // list built backwards and reversed: `cut` walks down from the end while calls still fit and
            // ends as the position of the oldest admitted call. That makes the contiguous-newest-tail
            // guarantee structural — it is what a suffix IS — instead of something that has to be
            // inferred from a latch, a reverse walk and a final Reverse() agreeing with each other.
            var cut = ordered.Count;
            while (cut > 0)
            {
                var cost = CostOf(ordered[cut - 1]);

                if (spent + cost > maxReplayedChars)
                {
                    budgetExhausted = true;
                    break;
                }

                spent += cost;
                cut--;
            }

            dropped += cut;
            admittedCalls += ordered.Count - cut;
            admittedByRow[i] = cut == 0 ? ordered : ordered.GetRange(cut, ordered.Count - cut);
        }

        if (dropped > 0)
        {
            logger?.LogWarning(
                "[ToolCallReplay] Replayed tool-call history exceeded the {Budget}-char window budget; " +
                "dropped the {Dropped} oldest call(s), replaying {Spent} chars.",
                maxReplayedChars, dropped, spent);
        }

        return (admittedByRow, admittedCalls);
    }

    /// <summary>
    /// What one record costs against the window budget: every part of it that reaches the model.
    /// </summary>
    /// <remarks>
    /// <see cref="ToolCallRecord.ToolName"/> and <see cref="ToolCallRecord.CallId"/> are counted even
    /// though they are normally short, because neither is bounded by
    /// <c>IToolCallReplayTreatment.Treat</c> — they come from the model or an MCP server and are
    /// persisted verbatim, so their size is not this code's to assume.
    /// </remarks>
    private static int CostOf(ToolCallRecord call) =>
        (call.Input?.Length ?? 0)
        + (call.Output?.Length ?? 0)
        + (call.ToolName?.Length ?? 0)
        + (call.CallId?.Length ?? 0);

    /// <summary>
    /// Appends one assistant/tool message pair per call, so a resumed conversation replays tool
    /// activity in the sequence it actually happened.
    /// </summary>
    /// <param name="result">The projection being built.</param>
    /// <param name="toolCalls">
    /// This row's admitted calls, already sorted by <see cref="ToolCallRecord.RoundOrdinal"/> and
    /// already filtered against the window budget by <see cref="SelectCallsWithinBudget"/>. Empty when
    /// the budget dropped all of them, in which case this appends nothing.
    /// </param>
    /// <param name="usedCallIds">Call ids already emitted anywhere in this window.</param>
    private static void AppendToolCallRounds(
        List<ChatMessage> result,
        IReadOnlyList<ToolCallRecord> toolCalls,
        HashSet<string> usedCallIds)
    {
        foreach (var call in toolCalls)
        {
            var callId = ResolveUniqueCallId(call.CallId, usedCallIds);

            result.Add(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, call.ToolName, ReconstructArguments(call.Input))]));
            result.Add(new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(callId, call.Output)]));
        }
    }

    /// <summary>
    /// Returns a call id unique across the whole replayed window, registering it in
    /// <paramref name="usedCallIds"/>, and synthesizing a replacement when the persisted id is absent
    /// or already taken.
    /// </summary>
    /// <remarks>
    /// Two distinct inputs need a synthesized id, for one shared reason. An <em>absent</em> id comes
    /// from a record persisted before this field existed. A <em>colliding</em> id comes from a provider
    /// that numbers call ids per-turn and resets them, so two turns each persisted <c>call_0</c>. Either
    /// way, emitting the id as-is would put two tool-call entries carrying one id into a single request,
    /// which providers reject — permanently, since the window is rebuilt from persisted rows on every
    /// later turn. The caller uses the returned id for <em>both</em> the call and its matching result,
    /// so the pair stays correlated whichever branch produced it.
    /// </remarks>
    private static string ResolveUniqueCallId(string? persistedCallId, HashSet<string> usedCallIds)
    {
        if (!string.IsNullOrEmpty(persistedCallId) && usedCallIds.Add(persistedCallId))
        {
            return persistedCallId;
        }

        // Loops on the same Add that registers it, so "unique" is a guarantee rather than a
        // near-certainty — the retry is free and costs two lines, where reasoning about Guid
        // collision odds at a correctness boundary costs a reader more.
        string synthesized;
        do
        {
            synthesized = $"replayed-{Guid.NewGuid():N}";
        }
        while (!usedCallIds.Add(synthesized));

        return synthesized;
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

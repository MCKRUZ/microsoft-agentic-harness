namespace Domain.Common.Config.AI.Conversations;

/// <summary>
/// Governs how much of a tool call's arguments/result text is replayed to the model verbatim when a
/// resumed conversation replays tool-call history, versus truncated or withheld outright. Bound from
/// <c>AppConfig:AI:Conversations:ToolCallReplay</c>.
/// </summary>
/// <remarks>
/// <para>
/// The size above which a single payload is withheld rather than truncated is a hard constant, not
/// exposed through config — above that ceiling the structural secret-redaction pass falls back to a
/// regex-only scan that cannot see through escaped-nested-JSON secrets (#391), so letting a
/// deployment raise it would let that deployment silently opt into replaying unredactable content to
/// the model. See the treatment service this config feeds for that constant and the three-tier rule
/// it implements.
/// </para>
/// <para>
/// The three settings bound three different quantities and none substitutes for another.
/// <see cref="MaxVerbatimChars"/> bounds <em>one</em> payload. <see cref="MaxCallsPerTurn"/> bounds
/// how many payloads a single turn <em>persists</em>. <see cref="MaxReplayedChars"/> bounds the total
/// a whole replayed window <em>sends to the model</em>. Only the last is a true ceiling on per-turn
/// prompt cost: the store's dispatch window is capped in rows, and one row expands into two chat
/// messages per tool call it carries, so a row cap alone stops bounding the prompt as soon as turns
/// are tool-heavy.
/// </para>
/// </remarks>
public sealed class ToolCallReplayConfig
{
    /// <summary>
    /// Whether replayed tool-call history is persisted at all. Defaults to <see langword="true"/> —
    /// a resumed conversation not remembering its own tool results is the defect this feature exists
    /// to fix, so shipping it default-off would mean it stays unexercised.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Above this many characters (post-treatment), a tool-call argument or result payload is
    /// truncated with a visible marker rather than replayed verbatim. Defaults to 8192 (roughly 2,000
    /// tokens) — a meaningful slice of a turn's context without letting one oversized tool payload
    /// dominate it. Clamped to <c>[0, ToolCallReplayTreatment.WithholdCeilingChars]</c> at the point
    /// it is used, defense-in-depth alongside the equivalent startup validation.
    /// </summary>
    public int MaxVerbatimChars { get; set; } = 8192;

    /// <summary>
    /// The most tool calls a single turn persists for replay. The <strong>newest</strong> this many are
    /// kept and the rest dropped, with a warning naming how many. Defaults to 32.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounds model-output-driven storage growth, which nothing else does: the agent framework's
    /// per-request iteration limit caps tool-calling <em>rounds</em>, not the calls issued in parallel
    /// within one round, and the chat client permits concurrent invocation. 32 is generous against real
    /// agent behaviour — a turn issuing more parallel calls than that is far more likely a runaway loop
    /// than legitimate work — while staying well clear of the point where one turn's history would
    /// dominate a resumed conversation. Set to 0 to persist no tool calls at all, though
    /// <see cref="Enabled"/> is the honest way to express that.
    /// </para>
    /// <para>
    /// Newest-kept is the same end <see cref="MaxReplayedChars"/> trims from on replay, and that
    /// agreement is load-bearing rather than incidental: the two settings are one policy enforced at two
    /// points, so trimming opposite ends would compose into a middle slice — neither the turn's opening
    /// reasoning nor its conclusions — and would break the contiguous-newest-tail property each half
    /// otherwise guarantees on its own.
    /// </para>
    /// </remarks>
    public int MaxCallsPerTurn { get; set; } = 32;

    /// <summary>
    /// The most treated tool-call text, in characters, that one replayed window sends back to the
    /// model across every row in it. Once exceeded, the oldest calls are dropped until the remainder
    /// fits, with a warning naming how many. Defaults to 65536 (roughly 16,000 tokens).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only setting that bounds what a resumed conversation actually costs per turn.
    /// Oldest-first is the drop order because a replayed window is context, not an audit log: the most
    /// recent tool activity is what the current turn is most likely to reason about, and dropping from
    /// the old end leaves a contiguous, coherent tail rather than holes through the middle.
    /// </para>
    /// <para>
    /// A call is dropped as a whole call/result pair, never half of one — a persisted assistant
    /// <c>tool_calls</c> entry with no matching result message is a malformed conversation a provider
    /// rejects outright, and because the window is rebuilt from persisted rows every turn, that
    /// rejection would recur permanently.
    /// </para>
    /// <para>
    /// <strong>Must be at least twice <see cref="MaxVerbatimChars"/>, and startup validation enforces
    /// that.</strong> One call costs up to two full payloads (arguments and result are capped
    /// separately), and admission latches shut at the first call that does not fit — so a window budget
    /// smaller than a single maximum-size call drops not merely that call but every older one behind
    /// it, emptying the conversation's whole replayed tool history for as long as the oversized call
    /// stays in the window. Raising <see cref="MaxVerbatimChars"/> for a large-context model without
    /// raising this in step is the mistake that rule exists to catch.
    /// </para>
    /// </remarks>
    public int MaxReplayedChars { get; set; } = 65536;
}

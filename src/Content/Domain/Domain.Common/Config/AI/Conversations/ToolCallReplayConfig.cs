namespace Domain.Common.Config.AI.Conversations;

/// <summary>
/// Governs how much of a tool call's arguments/result text is replayed to the model verbatim when a
/// resumed conversation replays tool-call history, versus truncated or withheld outright. Bound from
/// <c>AppConfig:AI:Conversations:ToolCallReplay</c>.
/// </summary>
/// <remarks>
/// Only <see cref="MaxVerbatimChars"/> is configurable here. The size above which a payload is
/// withheld rather than truncated is a hard constant, not exposed through config — above that
/// ceiling the structural secret-redaction pass falls back to a regex-only scan that cannot see
/// through escaped-nested-JSON secrets (#391), so letting a deployment raise it would let that
/// deployment silently opt into replaying unredactable content to the model. See the treatment
/// service this config feeds for that constant and the three-tier rule it implements.
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
}

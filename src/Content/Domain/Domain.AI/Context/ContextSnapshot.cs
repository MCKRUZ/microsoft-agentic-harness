namespace Domain.AI.Context;

/// <summary>
/// The state of the model's context window immediately after turn
/// <paramref name="TurnIndex"/> completes. <see cref="CtxAfter"/> is the
/// per-category total at that moment; <see cref="Loaded"/> is the delta
/// for this specific turn. Per foresight-dashboard-spec.md §6.6 the invariant holds:
/// <c>CtxAfter[N] = CtxAfter[N-1] + sum(Loaded[N] by category)</c>.
/// </summary>
/// <param name="ConversationId">Stable conversation identifier; matches the SignalR group name.</param>
/// <param name="TurnIndex">Zero-based index of the turn within the conversation.</param>
/// <param name="TurnId">Stable id of the turn (e.g. "t-01") for cross-reference with stored messages.</param>
/// <param name="CtxAfter">Cumulative breakdown after this turn lands.</param>
/// <param name="Loaded">Artifacts added by this turn (the per-turn delta).</param>
/// <param name="CapturedAtUtc">Server clock at capture; clients show this in the timeline.</param>
/// <param name="MeasuredInputTokens">
/// What the provider actually billed for this turn's prompt, or <c>0</c> when no usage was reported.
/// </param>
public sealed record ContextSnapshot(
    string ConversationId,
    int TurnIndex,
    string TurnId,
    CategoryBreakdown CtxAfter,
    IReadOnlyList<LoadedItem> Loaded,
    DateTimeOffset CapturedAtUtc,
    int MeasuredInputTokens = 0)
{
    /// <summary>
    /// <see cref="MeasuredInputTokens"/> minus <see cref="CategoryBreakdown.Total"/>: the part of the
    /// real prompt no category accounts for. Positive means context reached the model that the harness
    /// never attributed; negative means the estimate overshot what was actually billed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published as a derived difference rather than a seventh <see cref="ContextCategory"/>, and the
    /// negative case is why. Categories are additive token counts — an overshoot has no representation
    /// as one, and overshoot is the failure this exists to surface: the breakdown's estimates come from
    /// a ~4-chars-per-token rule that runs long on JSON-shaped tool payloads, so on a tool-heavy turn
    /// the attributed total can exceed what the provider charged.
    /// </para>
    /// <para>
    /// That overshoot used to be invisible. <c>System</c> was computed as
    /// <c>max(0, billed − messages)</c> — a residual, never measured — so an overshoot silently floored
    /// the whole system-prompt segment to zero, indistinguishable from a turn with no system prompt at
    /// all (#507). Every category is now measured from what was actually loaded, and this is the one
    /// number left over. A reader who wants to know how far to trust the bar reads this.
    /// </para>
    /// <para>
    /// Reads <c>0</c> when <see cref="MeasuredInputTokens"/> is <c>0</c> — no usage was reported, so
    /// there is nothing to reconcile against and a large fake gap would be worse than none.
    /// </para>
    /// </remarks>
    public int UnaccountedTokens =>
        MeasuredInputTokens == 0 ? 0 : MeasuredInputTokens - CtxAfter.Total;
}

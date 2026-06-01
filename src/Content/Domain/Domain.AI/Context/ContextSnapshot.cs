namespace Domain.AI.Context;

/// <summary>
/// The state of the model's context window immediately after turn
/// <paramref name="TurnIndex"/> completes. <see cref="CtxAfter"/> is the
/// per-category total at that moment; <see cref="Loaded"/> is the delta
/// for this specific turn. Per HANDOFF.md §6.6 the invariant holds:
/// <c>CtxAfter[N] = CtxAfter[N-1] + sum(Loaded[N] by category)</c>.
/// </summary>
/// <param name="ConversationId">Stable conversation identifier; matches the SignalR group name.</param>
/// <param name="TurnIndex">Zero-based index of the turn within the conversation.</param>
/// <param name="TurnId">Stable id of the turn (e.g. "t-01") for cross-reference with stored messages.</param>
/// <param name="CtxAfter">Cumulative breakdown after this turn lands.</param>
/// <param name="Loaded">Artifacts added by this turn (the per-turn delta).</param>
/// <param name="CapturedAtUtc">Server clock at capture; clients show this in the timeline.</param>
public sealed record ContextSnapshot(
    string ConversationId,
    int TurnIndex,
    string TurnId,
    CategoryBreakdown CtxAfter,
    IReadOnlyList<LoadedItem> Loaded,
    DateTimeOffset CapturedAtUtc);

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
/// <remarks>
/// <para>
/// <strong>There is deliberately no "unaccounted" figure here</strong>, though #507 originally called
/// for one. Reconciling the breakdown against what the provider actually billed needs a measurement of
/// <em>this turn's prompt</em>, and the harness does not have one: <c>ILlmUsageCapture</c> accumulates
/// input tokens across every model call in a turn, so a turn with two tool round-trips reports roughly
/// three prompts' worth. Subtracting a single post-turn context total from that produces a large
/// positive number with no meaning — a confidently wrong figure on a dashboard, which is precisely the
/// defect #507 exists to remove. Tracked as a follow-up; it needs per-call prompt sizing first.
/// </para>
/// </remarks>
public sealed record ContextSnapshot(
    string ConversationId,
    int TurnIndex,
    string TurnId,
    CategoryBreakdown CtxAfter,
    IReadOnlyList<LoadedItem> Loaded,
    DateTimeOffset CapturedAtUtc);

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
/// <param name="UnattributedTokens">
/// Signed reconciliation gap between what the bar attributed and what the provider actually billed
/// for the turn's last model call (#517): <c>lastCallPromptTokens - CtxAfter.Total</c>. Positive means
/// context reached the model that no lane explains; negative means the bar's own estimates overshot
/// the real prompt. <see langword="null"/> when no model call landed this turn (a failed turn, or one
/// with only local work) — there is nothing to reconcile against. <strong>Deliberately not a seventh
/// <see cref="ContextCategory"/></strong>: a category is an additive token count and cannot represent
/// an overshoot, and adding one would drag in every hand-maintained mirror of the enum the same way
/// adding a <c>RedactionCategory</c> member did (see <c>CLAUDE.md</c>'s Common Mistakes).
/// </param>
/// <remarks>
/// <para>
/// #507 originally called for a reconciliation figure and removed it before merge: the only usage
/// measurement then available was <c>ILlmUsageCapture</c>'s accumulated total across every model call
/// in the turn, so a turn with two tool round-trips reported roughly three prompts' worth — subtracted
/// against a single post-turn context total, that produced a large positive number with no meaning.
/// <see cref="UnattributedTokens"/> exists now because that gap is closed on both operands: it compares
/// the <em>last</em> call's own prompt tokens (not the accumulated total) against <see cref="CtxAfter"/>
/// computed from the same pre-response history that call actually saw, not the post-turn history that
/// includes this turn's own not-yet-sent assistant reply. Both were the two "same side of the turn
/// boundary" gaps #517 tracked.
/// </para>
/// <para>
/// One real, known gap still shows up here as a nonzero figure rather than a false one: intra-turn
/// tool-round-trip messages (a tool call and its result, exchanged mid-turn) are invisible to
/// <see cref="CategoryBreakdown.Messages"/> the same way they are to
/// <c>RegistrationBreakdownCalculator.TokensFor(SkillRegistration)</c>'s own remarks — whether they
/// ever land in the estimated transcript depends on the durable tool-call replay path, not on
/// anything this turn does. On a tool-heavy turn that content genuinely was billed as input, so a
/// positive <see cref="UnattributedTokens"/> there is a true reconciliation signal, not noise —
/// exactly the "context reached the model that no lane explains" case this field exists to surface.
/// </para>
/// </remarks>
public sealed record ContextSnapshot(
    string ConversationId,
    int TurnIndex,
    string TurnId,
    CategoryBreakdown CtxAfter,
    IReadOnlyList<LoadedItem> Loaded,
    DateTimeOffset CapturedAtUtc,
    int? UnattributedTokens = null);

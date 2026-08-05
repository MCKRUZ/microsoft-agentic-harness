using Domain.AI.Budget;

namespace Application.AI.Common.Interfaces.AI;

/// <summary>
/// Tracks cumulative token consumption across <em>all turns of a single conversation</em> and reports
/// when a conversation has exhausted its lifetime budget so the caller can stop gracefully.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a <strong>singleton</strong> keyed by an arbitrary caller-supplied string: a conversation
/// spans many turns (each its own MediatR request scope), so the tracker must outlive any one scope —
/// unlike the per-turn scoped <see cref="ITokenBudgetTracker"/>, which caps a single turn and is re-seeded
/// each request. The two are complementary: <see cref="ITokenBudgetTracker"/> bounds intra-turn cost (and
/// throws on a pre-flight overage); this tracker bounds whole-conversation cost and is consulted
/// <em>between</em> turns to break the loop gracefully — it never throws.
/// </para>
/// <para>
/// <strong>The key is not necessarily a conversation id.</strong> Plan runs deliberately namespace their
/// own keys (<c>Domain.AI.Planner.PlanRunKeys.RunBudgetKey</c>) so a plan's budget cannot collide with, or
/// be erased by, a conversation sharing the same identifier. Implementations must therefore treat the key
/// as an opaque string and never join it against conversation storage.
/// </para>
/// <para>
/// <strong>Asynchronous because a total may be shared.</strong> When AgentHub and the Execution API run in
/// separate processes, each host enforcing a private copy of one ceiling lets a conversation spend roughly
/// double it. A durable implementation therefore reads shared state on <em>every</em> gate check, and any
/// in-process cache in front of that read reintroduces the very divergence it exists to remove.
/// </para>
/// <para>
/// Implementations are thread-safe and bound their storage: a long-lived deployment can accumulate many
/// keys, so entries are reclaimed (by eviction in memory, or by retention over a durable store) rather than
/// retained forever.
/// </para>
/// </remarks>
public interface IConversationBudgetTracker
{
    /// <summary>
    /// Adds a completed turn's token usage to the key's running total, creating the entry
    /// (seeded from configuration) on first use.
    /// </summary>
    /// <param name="budgetKey">The opaque budget key the usage belongs to.</param>
    /// <param name="tokensUsed">Input+output tokens consumed by the turn. Non-negative; zero is a no-op.</param>
    /// <param name="cancellationToken">Cancels the accrual.</param>
    Task RecordUsageAsync(string budgetKey, int tokensUsed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the key's current budget status. When no budget is configured, or the key has no recorded
    /// usage yet, returns a status whose <see cref="ConversationBudgetStatus.IsExhausted"/> reflects the
    /// configured ceiling (disabled ceilings never report exhausted).
    /// </summary>
    /// <param name="budgetKey">The opaque budget key to query.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ConversationBudgetStatus> GetStatusAsync(string budgetKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the key's tracked usage, freeing its entry.
    /// </summary>
    /// <remarks>
    /// Call this only when the caller genuinely owns the key's whole lifetime and knows it has ended — a
    /// plan run does; a single turn of a conversation does not, because the conversation continues in
    /// another run or another host. Releasing a key that is still live erases the accumulated total and
    /// silently resets the ceiling. Safe to call for an unknown key.
    /// </remarks>
    /// <param name="budgetKey">The opaque budget key to release.</param>
    /// <param name="cancellationToken">Cancels the release.</param>
    Task ReleaseAsync(string budgetKey, CancellationToken cancellationToken = default);
}

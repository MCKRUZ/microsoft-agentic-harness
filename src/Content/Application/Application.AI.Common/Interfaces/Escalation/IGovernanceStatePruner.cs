namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Retention for the durable governance-state store. Terminal records — resolved escalations,
/// closed change proposals, and call-once ledger claims — accumulate forever otherwise; this
/// deletes those older than the configured retention window.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate contract rather than methods on <c>IEscalationStateStore</c> and
/// <c>IChangeProposalStore</c>: retention spans every table in the store, is an operator
/// concern rather than a workflow concern, and <c>IChangeProposalStore</c> is a shared
/// contract this work is not permitted to widen.
/// </para>
/// <para>
/// Only terminal rows are eligible for the escalation and change-proposal tables. A pending
/// escalation or an in-flight proposal is never pruned regardless of age — losing one would
/// strand an approval, which is the exact failure the durable store exists to prevent. A
/// call-once ledger row has no such lifecycle: the moment it is written it already IS the
/// terminal fact ("this tool ran"), so age alone is eligibility. Compliance history is
/// unaffected either way: the JSONL audit stores are the retained record, and this prunes only
/// the recoverable working state.
/// </para>
/// </remarks>
public interface IGovernanceStatePruner
{
    /// <summary>
    /// Deletes terminal governance-state records last updated before <paramref name="cutoff"/>.
    /// </summary>
    /// <param name="cutoff">Records older than this instant are eligible for deletion.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were removed from each table.</returns>
    Task<GovernanceStatePruneResult> PruneAsync(DateTimeOffset cutoff, CancellationToken ct);
}

/// <summary>The row counts removed by one <see cref="IGovernanceStatePruner.PruneAsync"/> pass.</summary>
/// <param name="EscalationsRemoved">Terminal escalation-state rows deleted.</param>
/// <param name="ChangeProposalsRemoved">Terminal change-proposal rows deleted.</param>
/// <param name="ToolCallLedgerRowsRemoved">Call-once ledger rows deleted.</param>
public sealed record GovernanceStatePruneResult(
    int EscalationsRemoved, int ChangeProposalsRemoved, int ToolCallLedgerRowsRemoved = 0);

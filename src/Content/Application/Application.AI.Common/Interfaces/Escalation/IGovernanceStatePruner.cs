namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Retention for the durable governance-state store. Terminal records — resolved escalations
/// and closed change proposals — accumulate forever otherwise; this deletes those older than
/// the configured retention window.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate contract rather than methods on <c>IEscalationStateStore</c> and
/// <c>IChangeProposalStore</c>: retention spans both tables, is an operator concern rather
/// than a workflow concern, and <c>IChangeProposalStore</c> is a shared contract this work is
/// not permitted to widen.
/// </para>
/// <para>
/// Only terminal rows are eligible. A pending escalation or an in-flight proposal is never
/// pruned regardless of age — losing one would strand an approval, which is the exact failure
/// the durable store exists to prevent. Compliance history is unaffected: the JSONL audit
/// stores are the retained record, and this prunes only the recoverable working state.
/// </para>
/// <para>
/// <strong>The call-once ledger (<c>tool_call_ledger</c>) is not covered by this contract at
/// all.</strong> A ledger row is not an audit record of a completed workflow — it IS the
/// enforcement token for a still-possibly-live call-once tool, so age alone is not eligibility
/// the way it is for the two tables above. See <c>GovernanceStatePruner</c>'s remarks.
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
public sealed record GovernanceStatePruneResult(int EscalationsRemoved, int ChangeProposalsRemoved);

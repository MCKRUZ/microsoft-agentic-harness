using Domain.AI.Changes;

namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// Persistence contract for <see cref="ChangeProposal"/> aggregates. Backs the
/// CQRS handlers (Get/List/Submit/Approve/Reject/Cancel) and the
/// <c>ChangeProposalOrchestrator</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to be idempotent on <see cref="SaveAsync"/>:
/// saving a proposal with an id that already exists overwrites the prior version.
/// Combined with the deterministic id from <c>ChangeProposalIdDeriver</c>, this
/// makes re-submission of the same logical change a no-op (the prior proposal's
/// state is preserved).
/// </para>
/// <para>
/// Default implementation in PR-2 is in-memory; consumers can register an
/// EF-Core-backed store (analogous to <c>EfCorePlanStateStore</c>) for production.
/// Wire-up lives in PR-2's Step 7 DI registration.
/// </para>
/// </remarks>
public interface IChangeProposalStore
{
    /// <summary>
    /// Fetch a proposal by its deterministic id.
    /// </summary>
    /// <param name="id">The proposal id (Base64URL SHA-256 from <c>ChangeProposalIdDeriver</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The proposal, or null when no proposal with that id is known.</returns>
    Task<ChangeProposal?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Persist a proposal. Overwrites any prior version with the same
    /// <see cref="ChangeProposal.Id"/>. Idempotent on duplicate saves of the same state.
    /// </summary>
    /// <param name="proposal">The proposal to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerate proposals matching the filter, capped at <see cref="ChangeProposalQuery.MaxResults"/>.
    /// </summary>
    /// <param name="query">Filter criteria. All criteria combine with AND; null filter dimensions match everything.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching proposals, ordered most-recently-submitted first.</returns>
    Task<IReadOnlyList<ChangeProposal>> ListAsync(
        ChangeProposalQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every evidence hash the gate decisions of an open proposal (not merged, rejected or cancelled) point at (#901).
    /// </summary>
    /// <remarks>
    /// Read by evidence retention, which must never delete evidence an open proposal still needs, so this reads every open
    /// proposal: no result cap, and a stored proposal that fails its integrity check counts too, because a quarantined
    /// record is kept for investigation and so is what it points at. One that cannot be read at all throws rather than
    /// being left out.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hashes.</returns>
    Task<IReadOnlySet<string>> GetEvidenceHashesOfOpenProposalsAsync(CancellationToken cancellationToken);
}

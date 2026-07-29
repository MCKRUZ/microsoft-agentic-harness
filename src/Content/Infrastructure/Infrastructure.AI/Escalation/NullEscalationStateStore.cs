using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// No-op <see cref="IEscalationStateStore"/> registered when durable escalation state is
/// disabled (<c>AppConfig:AI:Governance:DurableState:EscalationsEnabled</c> = false, the
/// default). Every write completes successfully without persisting anything and every read
/// returns empty, which preserves the pre-durability, in-memory-only behavior of
/// <see cref="DefaultEscalationService"/> exactly: no database file is created, no write can
/// fail, and restarts lose pending escalations — the documented in-memory contract.
/// </summary>
public sealed class NullEscalationStateStore : IEscalationStateStore
{
    /// <inheritdoc />
    public Task SavePendingAsync(EscalationRequest request, DateTimeOffset createdAt, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task SaveDecisionsAsync(Guid escalationId, IReadOnlyList<ApproverDecision> decisions, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task MarkResolvedPendingAuditAsync(EscalationOutcome outcome, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task MarkResolvedAsync(Guid escalationId, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveAsync(Guid escalationId, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Always grants the claim. With no durable store there is no cross-pass state to
    /// coordinate; the escalation service's in-memory claim flag is the only arbiter, and it
    /// already serializes reconcile passes within the process.
    /// </summary>
    /// <param name="escalationId">Ignored.</param>
    /// <param name="staleClaimBefore">Ignored — there are no durable claims to expire.</param>
    /// <param name="ct">Ignored.</param>
    /// <returns>Always true.</returns>
    public Task<bool> TryClaimResolvedPendingAuditAsync(
        Guid escalationId, DateTimeOffset staleClaimBefore, CancellationToken ct)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task ReleaseClaimAsync(Guid escalationId, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<EscalationStateSnapshot>> GetActiveAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<EscalationStateSnapshot>>([]);

    /// <inheritdoc />
    public Task<EscalationOutcome?> GetResolvedOutcomeAsync(Guid escalationId, CancellationToken ct)
        => Task.FromResult<EscalationOutcome?>(null);
}

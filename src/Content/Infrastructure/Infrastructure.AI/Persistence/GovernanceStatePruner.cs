using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Changes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IGovernanceStatePruner"/>: deletes terminal
/// governance-state rows older than the retention cutoff.
/// </summary>
/// <remarks>
/// <para>
/// Eligibility is deliberately narrow. Escalations must be in
/// <see cref="EscalationPersistedStatus.Resolved"/> — a pending escalation, or one parked
/// awaiting reconciliation, is never pruned no matter how old, because deleting it would
/// strand an approval or discard an unaudited verdict. Change proposals must be in a terminal
/// <see cref="ChangeProposalStatus"/>; anything still moving through the gate pipeline stays.
/// </para>
/// <para>
/// Compliance history is untouched: the hash-chained JSONL audit stores are the retained
/// record. This prunes only the recoverable working state, which has no value once its
/// workflow has finished.
/// </para>
/// </remarks>
public sealed class GovernanceStatePruner : IGovernanceStatePruner
{
    private readonly IDbContextFactory<GovernanceStateDbContext> _contextFactory;
    private readonly ILogger<GovernanceStatePruner> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="contextFactory">Factory for short-lived governance-state contexts.</param>
    /// <param name="schemaInitializer">
    /// Forces schema creation before the first prune. Unused beyond its construction side effect.
    /// </param>
    /// <param name="logger">Structured logger.</param>
    public GovernanceStatePruner(
        IDbContextFactory<GovernanceStateDbContext> contextFactory,
        GovernanceStateSchemaInitializer schemaInitializer,
        ILogger<GovernanceStatePruner> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GovernanceStatePruneResult> PruneAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var cutoffTicks = cutoff.UtcTicks;
        var terminalProposalStatuses = ChangeProposalStateTransitions.TerminalStates
            .Select(s => s.ToString())
            .ToArray();

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var escalationsRemoved = await context.Escalations
            .Where(e => e.Status == nameof(EscalationPersistedStatus.Resolved)
                && e.UpdatedAtTicks < cutoffTicks)
            .ExecuteDeleteAsync(ct);

        var proposalsRemoved = await context.ChangeProposals
            .Where(p => terminalProposalStatuses.Contains(p.Status)
                && p.SubmittedAtTicks < cutoffTicks)
            .ExecuteDeleteAsync(ct);

        if (escalationsRemoved > 0 || proposalsRemoved > 0)
        {
            _logger.LogInformation(
                "Pruned governance state older than {Cutoff}: {EscalationCount} escalation(s), {ProposalCount} proposal(s)",
                cutoff, escalationsRemoved, proposalsRemoved);
        }

        return new GovernanceStatePruneResult(escalationsRemoved, proposalsRemoved);
    }
}

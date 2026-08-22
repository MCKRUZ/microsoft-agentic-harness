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
/// Eligibility is deliberately narrow for the two workflow tables. Escalations must be in
/// <see cref="EscalationPersistedStatus.Resolved"/> — a pending escalation, or one parked
/// awaiting reconciliation, is never pruned no matter how old, because deleting it would
/// strand an approval or discard an unaudited verdict. Change proposals must be in a terminal
/// <see cref="ChangeProposalStatus"/>; anything still moving through the gate pipeline stays.
/// The call-once ledger has no such filter: a row records a fact with no lifecycle ("this tool
/// already ran here"), so age alone determines eligibility.
/// </para>
/// <para>
/// Compliance history is untouched: the hash-chained JSONL audit stores are the retained
/// record. This prunes only the recoverable working state, which has no value once its
/// workflow has finished.
/// </para>
/// </remarks>
public sealed class GovernanceStatePruner : IGovernanceStatePruner
{
    // The terminal set is fixed at compile time; materializing it per pass would rebuild the same
    // array on every retention tick for the lifetime of the host.
    private static readonly string[] TerminalProposalStatuses = ChangeProposalStateTransitions
        .TerminalStates
        .Select(s => s.ToString())
        .ToArray();

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
        SchemaInitializer<GovernanceStateDbContext> schemaInitializer,
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

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var escalationsRemoved = await context.Escalations
            .Where(e => e.Status == nameof(EscalationPersistedStatus.Resolved)
                && e.UpdatedAtTicks < cutoffTicks)
            .ExecuteDeleteAsync(ct);

        var proposalsRemoved = await context.ChangeProposals
            .Where(p => TerminalProposalStatuses.Contains(p.Status)
                && p.SubmittedAtTicks < cutoffTicks)
            .ExecuteDeleteAsync(ct);

        // No status filter: a ledger row has no lifecycle to be mid-way through — the moment it
        // exists it already is the terminal fact ("this tool ran in this conversation"). Age alone
        // is eligibility.
        var ledgerRowsRemoved = await context.ToolCallLedger
            .Where(l => l.CalledAtTicks < cutoffTicks)
            .ExecuteDeleteAsync(ct);

        if (escalationsRemoved > 0 || proposalsRemoved > 0 || ledgerRowsRemoved > 0)
        {
            _logger.LogInformation(
                "Pruned governance state older than {Cutoff}: {EscalationCount} escalation(s), "
                + "{ProposalCount} proposal(s), {LedgerCount} ledger claim(s)",
                cutoff, escalationsRemoved, proposalsRemoved, ledgerRowsRemoved);
        }

        return new GovernanceStatePruneResult(escalationsRemoved, proposalsRemoved, ledgerRowsRemoved);
    }
}

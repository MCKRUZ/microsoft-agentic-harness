using Application.AI.Common.Interfaces.Governance;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Governance;

/// <summary>
/// SQLite-backed <see cref="IToolCallLedger"/> over <see cref="GovernanceStateDbContext"/>.
/// Registered as the active ledger when
/// <c>AppConfig:AI:Governance:DurableState:CallOnceEnforcementEnabled</c> is true.
/// </summary>
/// <remarks>
/// <see cref="TryClaimAsync"/> is a bare insert, not a query followed by an insert: the
/// composite primary key on <see cref="ToolCallLedgerEntity"/> is the enforcement mechanism, and
/// letting SQLite reject a duplicate is what keeps the check-and-claim atomic under a parallel
/// batch of calls to the same tool within one assistant message. Matches
/// <c>EfCoreConversationStore.IsDuplicateConversationId</c>'s reasoning for accepting either
/// <see cref="SqliteErrorCodes"/> value: which one SQLite reports depends on how the key is
/// declared, not on anything the caller controls.
/// </remarks>
public sealed class EfCoreToolCallLedger : IToolCallLedger
{
    private readonly IDbContextFactory<GovernanceStateDbContext> _contextFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<EfCoreToolCallLedger> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="contextFactory">Factory for short-lived governance-state contexts.</param>
    /// <param name="schemaInitializer">
    /// Forces schema creation and evolution before the first operation. Unused beyond its
    /// construction side effect — mirrors <see cref="Escalation.EfCoreEscalationStateStore"/>.
    /// </param>
    /// <param name="time">Clock for the diagnostic <c>CalledAtTicks</c> column.</param>
    /// <param name="logger">Structured logger.</param>
    public EfCoreToolCallLedger(
        IDbContextFactory<GovernanceStateDbContext> contextFactory,
        SchemaInitializer<GovernanceStateDbContext> schemaInitializer,
        TimeProvider time,
        ILogger<EfCoreToolCallLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(string scopeId, string toolName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // The entity's column is still named ConversationId (an additive rename would mean a real
        // migration, not just an interface change) — it holds whatever IAgentExecutionContext
        // .CallOnceScopeId supplied: a durable conversation id for an agent turn, or a run id for a
        // workflow run. See IAgentExecutionContext.CallOnceScopeId's remarks for why those are two
        // different values now, not one reused for both purposes.
        context.ToolCallLedger.Add(new ToolCallLedgerEntity
        {
            ConversationId = scopeId,
            ToolName = toolName,
            CalledAtTicks = _time.GetUtcNow().UtcTicks
        });

        try
        {
            await context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicateClaim(ex))
        {
            _logger.LogInformation(
                "Call-once tool {ToolName} refused a second call in scope {ScopeId}.",
                toolName, scopeId);
            return false;
        }
        catch (DbUpdateException ex)
        {
            // Fail closed on any other write failure too (disk full, locked file, a transient SQLite
            // busy error) — a claim this method could not durably record must be treated as "not
            // proven safe to allow," the same fail-closed reasoning the rest of this codebase's
            // governance surfaces already apply. The bool return can't distinguish this from a
            // genuine duplicate; CallOnceGate's denial message is worded to stay true under either
            // cause rather than assert "already called" when it might only be "could not verify."
            _logger.LogError(ex,
                "Call-once claim for {ToolName} in scope {ScopeId} could not be recorded; refusing " +
                "the call rather than risking an unrecorded, unenforced repeat.",
                toolName, scopeId);
            return false;
        }
    }

    private static bool IsDuplicateClaim(DbUpdateException ex) =>
        ex.InnerException is SqliteException
        {
            SqliteExtendedErrorCode: SqliteErrorCodes.ConstraintPrimaryKey or SqliteErrorCodes.ConstraintUnique
        };
}

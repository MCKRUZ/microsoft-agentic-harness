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
/// extended result code: which one SQLite reports depends on how the key is declared, not on
/// anything the caller controls.
/// </remarks>
public sealed class EfCoreToolCallLedger : IToolCallLedger
{
    /// <summary>SQLite's <c>SQLITE_CONSTRAINT_PRIMARYKEY</c> extended result code.</summary>
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>SQLite's <c>SQLITE_CONSTRAINT_UNIQUE</c> extended result code.</summary>
    private const int SqliteConstraintUnique = 2067;

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
    public async Task<bool> TryClaimAsync(string conversationId, string toolName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        context.ToolCallLedger.Add(new ToolCallLedgerEntity
        {
            ConversationId = conversationId,
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
                "Call-once tool {ToolName} refused a second call in conversation {ConversationId}.",
                toolName, conversationId);
            return false;
        }
    }

    private static bool IsDuplicateClaim(DbUpdateException ex) =>
        ex.InnerException is SqliteException
        {
            SqliteExtendedErrorCode: SqliteConstraintPrimaryKey or SqliteConstraintUnique
        };
}

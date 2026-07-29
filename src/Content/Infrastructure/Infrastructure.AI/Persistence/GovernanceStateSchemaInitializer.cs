using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Governance-state schema initializer: runs the base <c>EnsureCreated</c> lifecycle, then
/// applies idempotent, PRAGMA-guarded DDL for schema additions that <c>EnsureCreated</c>
/// cannot deliver to a pre-existing database.
/// </summary>
/// <remarks>
/// <para>
/// Follows <see cref="PlannerSchemaInitializer"/>. The base
/// <see cref="SchemaInitializer{TContext}"/> only calls <c>EnsureCreated</c>, which no-ops
/// once the database exists — so a consumer who created a governance-state database before
/// the outcome-seal columns shipped would never receive them, and the first reconcile scan
/// would fail with "no such column". Shipping the derived initializer from the start means
/// the next column addition has a place to live instead of silently breaking existing
/// deployments.
/// </para>
/// <para>
/// SQLite-specific by design (the governance-state registration is SQLite-only); the
/// evolution step is skipped for any other provider, where a fresh <c>EnsureCreated</c>
/// already contains every column.
/// </para>
/// </remarks>
public sealed class GovernanceStateSchemaInitializer : SchemaInitializer<GovernanceStateDbContext>
{
    /// <summary>
    /// Initializes a new instance: ensures the database exists (base), then ensures the
    /// outcome-seal columns and the composite status index exist on <c>escalation_state</c>.
    /// </summary>
    /// <param name="contextFactory">Factory for short-lived governance-state contexts.</param>
    public GovernanceStateSchemaInitializer(IDbContextFactory<GovernanceStateDbContext> contextFactory)
        : base(contextFactory)
    {
        using var context = contextFactory.CreateDbContext();
        if (context.Database.IsSqlite())
            EnsureSealColumns(context);
    }

    /// <summary>
    /// Adds the outcome-seal columns to <c>escalation_state</c> when a pre-existing database
    /// lacks them, and (re)creates the composite status index. Idempotent: guarded by
    /// <c>PRAGMA table_info</c> and <c>CREATE INDEX IF NOT EXISTS</c>. Existing rows keep
    /// <c>NULL</c> seals, which verification treats as unsealed and therefore not
    /// re-drivable — fail-closed, exactly as intended for rows written before sealing existed.
    /// </summary>
    /// <param name="context">An open governance-state context on the target database.</param>
    private static void EnsureSealColumns(GovernanceStateDbContext context)
    {
        var columns = ReadColumnNames(context);

        // EnsureCreated short-circuits when the database has ANY table, so a database file
        // with other tables but no escalation_state would reach this point with zero columns —
        // ALTER TABLE would then throw at startup. No table means nothing to evolve.
        if (columns.Count == 0)
            return;

        if (!columns.Contains("OutcomeSealJson"))
            context.Database.ExecuteSqlRaw("ALTER TABLE \"escalation_state\" ADD COLUMN \"OutcomeSealJson\" TEXT NULL;");

        var proposalColumns = ReadColumnNames(context, "change_proposal");
        if (proposalColumns.Count > 0 && !proposalColumns.Contains("ProposalSealJson"))
            context.Database.ExecuteSqlRaw("ALTER TABLE \"change_proposal\" ADD COLUMN \"ProposalSealJson\" TEXT NULL;");

        context.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS \"ix_escalation_state_status_updated_at\" " +
            "ON \"escalation_state\" (\"Status\", \"UpdatedAtTicks\");");
    }

    /// <summary>
    /// Reads the current column names of <c>escalation_state</c> via <c>PRAGMA table_info</c>
    /// (name is the second result column).
    /// </summary>
    /// <param name="context">An open governance-state context on the target database.</param>
    /// <returns>The case-insensitive set of existing column names.</returns>
    private static HashSet<string> ReadColumnNames(
        GovernanceStateDbContext context, string tableName = "escalation_state")
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        using var command = connection.CreateCommand();
        // Table names here are compile-time constants from this class, never user input.
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        return columns;
    }
}

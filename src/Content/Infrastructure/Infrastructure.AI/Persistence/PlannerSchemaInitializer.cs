using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Planner-specific schema initializer: runs the base EnsureCreated lifecycle, then applies
/// idempotent, PRAGMA-guarded DDL for schema additions that EnsureCreated cannot deliver to
/// a pre-existing database.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SchemaInitializer{TContext}"/> only calls <c>EnsureCreated</c>, which is a
/// no-op when the database already exists — so a template consumer who created a planner
/// database before the ownership columns shipped would never receive them, and the first
/// scope-filtered query would fail with "no such column". This subclass checks
/// <c>PRAGMA table_info(PlanGraphs)</c> and adds the missing <c>OwnerId</c>/<c>TenantId</c>
/// columns (plus the composite index) in place. Existing rows keep <c>NULL</c> ownership —
/// the documented "global record" semantics — so no backfill is needed.
/// </para>
/// <para>
/// SQLite-specific by design (the planner registration is SQLite-only); the evolution step
/// is skipped for any other provider, where a fresh EnsureCreated already contains the
/// columns. Kept planner-specific deliberately — the shared
/// <see cref="SchemaInitializer{TContext}"/> stays a pure EnsureCreated wrapper for the
/// other subsystems.
/// </para>
/// </remarks>
public sealed class PlannerSchemaInitializer : SchemaInitializer<PlannerDbContext>
{
    /// <summary>
    /// Initializes a new instance: ensures the database exists (base), then ensures the
    /// ownership columns and their composite index exist on <c>PlanGraphs</c>.
    /// </summary>
    /// <param name="contextFactory">Factory for short-lived planner contexts.</param>
    public PlannerSchemaInitializer(IDbContextFactory<PlannerDbContext> contextFactory)
        : base(contextFactory)
    {
        using var context = contextFactory.CreateDbContext();
        if (context.Database.IsSqlite())
            EnsureOwnershipColumns(context);
    }

    /// <summary>
    /// Adds <c>OwnerId</c>/<c>TenantId</c> to <c>PlanGraphs</c> when a pre-existing database
    /// lacks them, and (re)creates the composite scope index. Idempotent: guarded by
    /// <c>PRAGMA table_info</c> and <c>CREATE INDEX IF NOT EXISTS</c>.
    /// </summary>
    /// <param name="context">An open planner context on the target database.</param>
    private static void EnsureOwnershipColumns(PlannerDbContext context)
    {
        var columns = ReadColumnNames(context);

        // EnsureCreated short-circuits when the database has ANY table, so a database file
        // with other tables but no PlanGraphs would reach this point with zero columns —
        // ALTER TABLE would then throw at startup. No table means nothing to evolve.
        if (columns.Count == 0)
            return;

        if (!columns.Contains("OwnerId"))
            context.Database.ExecuteSqlRaw("ALTER TABLE \"PlanGraphs\" ADD COLUMN \"OwnerId\" TEXT NULL;");

        if (!columns.Contains("TenantId"))
            context.Database.ExecuteSqlRaw("ALTER TABLE \"PlanGraphs\" ADD COLUMN \"TenantId\" TEXT NULL;");

        context.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS \"IX_PlanGraphs_TenantId_OwnerId\" " +
            "ON \"PlanGraphs\" (\"TenantId\", \"OwnerId\");");
    }

    /// <summary>
    /// Reads the current column names of <c>PlanGraphs</c> via <c>PRAGMA table_info</c>
    /// (name is the second result column).
    /// </summary>
    /// <param name="context">An open planner context on the target database.</param>
    /// <returns>The case-insensitive set of existing column names.</returns>
    private static HashSet<string> ReadColumnNames(PlannerDbContext context)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"PlanGraphs\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        return columns;
    }
}

using System.Reflection;
using Infrastructure.Postgres.Migrations;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// The observability schema's migration set: where its ledger lives, and how to load its scripts.
/// </summary>
/// <remarks>
/// Public, and named once, because more than the store needs it. The integration test fixture brings
/// its database up to date through this same definition, so a test database and a production database
/// cannot end up being migrated by two different sets of rules — which was exactly the shape of the
/// defect this schema had before #301: CI applied the SQL one way, real installations another, and
/// only the CI path was ever exercised.
/// </remarks>
public static class ObservabilityMigrations
{
    /// <summary>
    /// Ledger table and advisory lock key for this migration set. The key is the ASCII bytes of
    /// <c>obs_migr</c>, chosen only so it is unlikely to collide with a key another subsystem picks
    /// in a shared database.
    /// </summary>
    public static PostgresMigrationOptions Options { get; } =
        new("schema_migrations", 0x6F62735F6D696772L);

    /// <summary>The assembly carrying the embedded observability migration scripts.</summary>
    public static Assembly ScriptAssembly => typeof(ObservabilityMigrations).Assembly;

    /// <summary>Loads the observability migration set in apply order.</summary>
    /// <returns>The migration scripts, ordered by their numeric file-name prefix.</returns>
    public static IReadOnlyList<MigrationScript> Load() =>
        EmbeddedSqlMigrationSource.Load(ScriptAssembly);
}

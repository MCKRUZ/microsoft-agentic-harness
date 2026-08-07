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
    /// <remarks>
    /// The ledger is <c>obs_schema_migrations</c>, not <c>schema_migrations</c>. The bare name is
    /// what Rails, Flyway and Django all use verbatim, so a consumer pointing the harness at a
    /// database their application already owns would have collided with a ledger that is not ours:
    /// the runner reads someone else's table, finds none of its own ids there, and applies its entire
    /// baseline. Prefixing costs nothing and the knowledge-graph set already did it.
    /// </remarks>
    public static PostgresMigrationOptions Options { get; } =
        new("obs_schema_migrations", 0x6F62735F6D696772L);

    /// <summary>Loads the observability migration set in apply order.</summary>
    /// <returns>The migration scripts, ordered by their numeric file-name prefix.</returns>
    public static IReadOnlyList<MigrationScript> Load() =>
        EmbeddedSqlMigrationSource.Load(typeof(ObservabilityMigrations).Assembly);
}

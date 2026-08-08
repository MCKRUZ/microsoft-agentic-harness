using Infrastructure.Postgres.Migrations;

namespace Infrastructure.AI.KnowledgeGraph.PostgreSql;

/// <summary>
/// The knowledge-graph schema's migration set: where its ledger lives, and how to load its scripts.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <c>ObservabilityMigrations</c>, and named publicly for the same reason: anything
/// that needs to bring a knowledge-graph database up to date — the store, an integration fixture, a
/// consumer's own provisioning step — must do it through one definition, or a test database and a
/// production database end up migrated by two different sets of rules. That divergence is the exact
/// shape of the defect #301 was filed about.
/// </para>
/// <para>
/// This started life as a private field and an inline load inside <c>PostgreSqlGraphStore</c>, which
/// meant nothing outside that class could name the set. The observability side had already learned
/// otherwise; the two are now at the same altitude, so whichever a future author reads first teaches
/// them the same pattern.
/// </para>
/// </remarks>
public static class KnowledgeGraphMigrations
{
    /// <summary>
    /// Ledger table and advisory lock key for this migration set.
    /// </summary>
    /// <remarks>
    /// The key is unchanged from when <c>PostgreSqlGraphStore</c> held its DDL inline and took this
    /// lock by hand, so a rolling deployment where both versions are briefly live still serializes
    /// their schema work against each other. The ledger name is prefixed rather than the bare
    /// <c>schema_migrations</c> that Rails, Flyway and Django all use, so pointing the harness at a
    /// database a consumer's own application already owns cannot collide with their ledger.
    /// </remarks>
    public static PostgresMigrationOptions Options { get; } =
        new("kg_schema_migrations", 0x6B675F736368656DL);

    /// <summary>Loads the knowledge-graph migration set in apply order.</summary>
    /// <returns>The migration scripts, ordered by their numeric file-name prefix.</returns>
    /// <remarks>
    /// Read once and reused; <see cref="MigrationScript"/> is immutable, so one instance is shareable.
    /// </remarks>
    public static IReadOnlyList<MigrationScript> Load() => Scripts;

    private static readonly IReadOnlyList<MigrationScript> Scripts =
        EmbeddedSqlMigrationSource.Load(typeof(KnowledgeGraphMigrations).Assembly);
}

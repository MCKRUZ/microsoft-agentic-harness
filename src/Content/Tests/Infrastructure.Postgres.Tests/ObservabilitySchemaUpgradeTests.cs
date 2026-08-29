using Domain.AI.Observability.Models;
using Infrastructure.Observability.Persistence;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// The test this whole issue exists for: proving a schema change reaches a database that already
/// holds data.
/// </summary>
/// <remarks>
/// <para>
/// Structured as a control and a treatment, following <c>SchemaInitializerAddedColumnTests</c> on the
/// SQLite side. The control is a database on the <em>previous</em> release asked to store the new
/// value; it must be rejected. Without that step the treatment proves only that a freshly built
/// database accepts <c>cancelled</c>, which the old mechanism could already do and which is exactly
/// why nobody noticed the gap for so long.
/// </para>
/// <para>
/// <strong>If a control ever stops failing, this runner is dead weight — delete it, do not keep
/// it.</strong> A control that passes means the value is already accepted without migration 005,
/// which would mean something else is delivering the schema and this machinery is redundant.
/// </para>
/// </remarks>
public sealed class ObservabilitySchemaUpgradeTests
{
    // Taken from the shipped options rather than re-typed. With a literal here, renaming the real
    // ledger left these tests green while they quietly migrated a table the product no longer writes.
    private static readonly string Ledger = ObservabilityMigrations.Options.LedgerTable;

    private const string InsertCancelledSession =
        """
        INSERT INTO sessions (conversation_id, agent_name, status)
        VALUES ('upgrade-probe', 'ProbeAgent', 'cancelled')
        """;

    /// <summary>
    /// The `sessions` table exactly as a pre-#301 installation has it: created by the old
    /// <c>init-db/01-schema.sql</c>, with the status CHECK written inline and therefore carrying
    /// whatever name Postgres generated for it, and with no migration ledger anywhere.
    /// </summary>
    private const string PreMigrationRunnerSchema =
        """
        CREATE TABLE sessions (
            id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            conversation_id       TEXT NOT NULL UNIQUE,
            agent_name            TEXT NOT NULL,
            model                 TEXT,
            started_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            ended_at              TIMESTAMPTZ,
            duration_ms           INTEGER,
            turn_count            INTEGER NOT NULL DEFAULT 0,
            tool_call_count       INTEGER NOT NULL DEFAULT 0,
            subagent_count        INTEGER NOT NULL DEFAULT 0,
            total_input_tokens    INTEGER NOT NULL DEFAULT 0,
            total_output_tokens   INTEGER NOT NULL DEFAULT 0,
            total_cache_read      INTEGER NOT NULL DEFAULT 0,
            total_cache_write     INTEGER NOT NULL DEFAULT 0,
            total_cost_usd        NUMERIC(10,6) NOT NULL DEFAULT 0,
            cache_hit_rate        NUMERIC(5,4) NOT NULL DEFAULT 0,
            status                TEXT NOT NULL DEFAULT 'active'
                                  CHECK (status IN ('active','completed','error')),
            error_message         TEXT,
            created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """;

    /// <summary>
    /// The case the whole idempotent-baseline strategy exists for, and the one every other test here
    /// was missing: a database whose tables were created by the ORIGINAL init-db SQL, with no ledger.
    /// </summary>
    /// <remarks>
    /// Every other case in this class builds its "old" database by running the migration runner with
    /// a truncated script list — so a ledger always exists and migration 001 is never replayed
    /// against tables that were already there. That is the interesting path: on a real consumer
    /// database 001 finds <c>sessions</c> present, <c>CREATE TABLE IF NOT EXISTS</c> no-ops, and the
    /// explicitly-named <c>sessions_status_check</c> that 001 declares is therefore never created.
    /// The upgrade only works because migration 005 discovers the constraint's real name from
    /// <c>pg_constraint</c> instead of assuming it. Nothing pinned that until this test.
    /// </remarks>
    [SkippableFact]
    public async Task ADatabaseBuiltByTheOldInitDbSql_IsBroughtForwardWithoutALedger()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        // Arrange: not through the runner. This is the shape no ledger has ever seen.
        await schema.ExecuteAsync(PreMigrationRunnerSchema);
        Assert.Equal(0, await schema.CountTablesAsync(Ledger));

        // Control: the old inline constraint is live and refuses the new word.
        var rejected = await schema.TryExecuteAsync(InsertCancelledSession);
        Assert.NotNull(rejected);

        // Treatment: the full set, replayed over pre-existing tables.
        var all = ObservabilityMigrations.Load();
        var applied = await schema.ApplyAsync(all, Ledger);
        Assert.Equal(all.Count, applied);

        Assert.Null(await schema.TryExecuteAsync(InsertCancelledSession));
        Assert.Equal("cancelled", await schema.ScalarAsync<string>(
            "SELECT status FROM sessions WHERE conversation_id = 'upgrade-probe'"));

        // And the rest of the set landed too — 001 no-oped on sessions but the later scripts did
        // real work on a database that had none of their objects.
        Assert.Equal(1, await schema.CountTablesAsync("context_snapshots"));
        Assert.Equal(1, await schema.CountColumnsAsync("session_messages", "content_full"));
    }

    /// <summary>
    /// The control for migration 005's <c>pg_constraint</c> lookup — a database whose status CHECK
    /// carries a name of the installation's own choosing rather than the one Postgres generates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was missing, and its absence made six lines of 005 indistinguishable from a comment.
    /// Postgres names an inline column CHECK <c>&lt;table&gt;_&lt;column&gt;_check</c> — character for
    /// character the name 005 declares — so for both databases this template itself produces, simply
    /// assuming the name works. Mutation-testing proved it: replacing 005's lookup with a hardcoded
    /// <c>DROP CONSTRAINT IF EXISTS sessions_status_check</c> left the entire suite green.
    /// </para>
    /// <para>
    /// The population #301 exists to reach is the database this template did <em>not</em> produce:
    /// one whose schema was hand-applied, adapted, or restored from a dump that named things
    /// differently. There a hardcoded DROP matches nothing and the ADD then <em>succeeds</em>, since
    /// the name it declares is not in use — so the migration reports success while the table ends up
    /// with two status constraints, the old narrow one still refusing <c>cancelled</c>. Nothing
    /// throws; the upgrade is simply a lie. This test is the only one here that can tell the lookup
    /// from the assumption.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task ADatabaseWhoseStatusConstraintWasHandNamed_IsStillBroughtForward()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        const string generatedName = "CHECK (status IN ('active','completed','error'))";
        const string handName =
            "CONSTRAINT sessions_status_allowed CHECK (status IN ('active','completed','error'))";

        var handNamed = PreMigrationRunnerSchema.Replace(generatedName, handName, StringComparison.Ordinal);

        // The rewrite is the entire premise. If it silently matched nothing this would just be the
        // test above wearing a more interesting name.
        Assert.NotEqual(PreMigrationRunnerSchema, handNamed);

        await schema.ExecuteAsync(handNamed);
        Assert.Equal("sessions_status_allowed", await schema.ScalarAsync<string>(StatusConstraintName));

        // Control: the hand-named constraint is live and refuses the new word, naming itself as it does.
        var rejected = await schema.TryExecuteAsync(InsertCancelledSession);
        Assert.NotNull(rejected);
        Assert.Contains("sessions_status_allowed", rejected!.ToString(), StringComparison.OrdinalIgnoreCase);

        // Treatment: the runner finds the constraint by definition rather than by name.
        await schema.ApplyAsync(ObservabilityMigrations.Load(), Ledger);

        Assert.Null(await schema.TryExecuteAsync(InsertCancelledSession));

        // Exactly one status constraint survives, and it is the one 005 declares — the old name was
        // dropped, not merely worked around.
        Assert.Equal("sessions_status_check", await schema.ScalarAsync<string>(StatusConstraintName));
    }

    /// <summary>
    /// The name of the CHECK constraint governing <c>sessions.status</c>, whatever it happens to be.
    /// Deliberately mirrors migration 005's own discovery query: a test that hardcoded the name could
    /// not detect 005 hardcoding it too.
    /// </summary>
    private const string StatusConstraintName =
        """
        SELECT con.conname
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
        WHERE rel.relname = 'sessions'
          AND nsp.nspname = current_schema()
          AND con.contype = 'c'
          AND pg_get_constraintdef(con.oid) ILIKE '%status%'
        """;

    [SkippableFact]
    public async Task ADatabaseOnThePreviousRelease_RejectsCancelled_UntilTheMigrationRunnerReachesIt()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var all = ObservabilityMigrations.Load();
        var previousRelease = all.Where(s => s.Ordinal < 5).ToArray();
        Assert.Equal(4, previousRelease.Length);

        // ---- Arrange: a database as a consumer running last month's harness would have it.
        await schema.ApplyAsync(previousRelease, Ledger);

        // ---- Control. This must fail, with the same instrument the treatment uses.
        var rejected = await schema.TryExecuteAsync(InsertCancelledSession);
        Assert.NotNull(rejected);
        Assert.Contains("sessions_status_check", rejected!.ToString(), StringComparison.OrdinalIgnoreCase);

        // ---- Treatment: the same database, brought forward by the runner.
        var applied = await schema.ApplyAsync(all, Ledger);
        Assert.Equal(all.Count - previousRelease.Length, applied); // every migration after 004 was outstanding

        Assert.Null(await schema.TryExecuteAsync(InsertCancelledSession));
        Assert.Equal("cancelled", await schema.ScalarAsync<string>(
            "SELECT status FROM sessions WHERE conversation_id = 'upgrade-probe'"));
    }

    [SkippableFact]
    public async Task TheWidenedConstraintStillRefusesAWordThatIsNotAStatus()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        await schema.ApplyAsync(ObservabilityMigrations.Load(), Ledger);

        // Migration 005 drops the old constraint before adding the new one. A version that dropped it
        // and failed to re-add would pass every assertion in the test above — the insert would
        // succeed — while having quietly removed the guard from the column entirely.
        var rejected = await schema.TryExecuteAsync(
            """
            INSERT INTO sessions (conversation_id, agent_name, status)
            VALUES ('bad-status-probe', 'ProbeAgent', 'errored')
            """);

        Assert.NotNull(rejected);
        Assert.Contains("sessions_status_check", rejected!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task EveryStatusTheCodeCanExpress_IsAcceptedByTheMigratedSchema()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        await schema.ApplyAsync(ObservabilityMigrations.Load(), Ledger);

        // Enumerated from the enum rather than listed, so adding a member without a migration fails
        // here instead of at a customer's first write — which is the failure mode #289 described.
        foreach (var status in Enum.GetValues<SessionStatus>())
        {
            var word = status.ToDbValue();
            var failure = await schema.TryExecuteAsync(
                $"""
                INSERT INTO sessions (conversation_id, agent_name, status)
                VALUES ('probe-{word}', 'ProbeAgent', '{word}')
                """);

            Assert.True(
                failure is null,
                $"The schema rejected '{word}', which SessionStatus.{status} can write: {failure}");
        }
    }

    [SkippableFact]
    public async Task ABaselineOnlyDatabase_IsBroughtAllTheWayForward()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        var all = ObservabilityMigrations.Load();

        // The oldest thing the runner itself can produce: only the baseline applied.
        await schema.ApplyAsync(all.Take(1).ToArray(), Ledger);
        Assert.Equal(0, await schema.CountTablesAsync("context_snapshots"));

        var applied = await schema.ApplyAsync(all, Ledger);

        Assert.Equal(all.Count - 1, applied);
        Assert.Equal(1, await schema.CountTablesAsync("context_snapshots"));
        Assert.Equal(1, await schema.CountColumnsAsync("session_messages", "content_full"));
    }
}

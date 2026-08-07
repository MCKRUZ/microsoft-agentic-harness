using Domain.AI.Observability.Models;
using Infrastructure.Observability.Persistence;
using Infrastructure.Postgres.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// The test this whole issue exists for: proving a schema change reaches a database that already
/// holds data.
/// </summary>
/// <remarks>
/// <para>
/// Structured as a control and a treatment, following <c>SchemaInitializerAddedColumnTests</c> on the
/// SQLite side. The control is a database migrated to the <em>previous</em> release and asked to
/// store the new value; it must be rejected. Without that step the treatment proves only that a
/// freshly built database accepts <c>cancelled</c>, which the old mechanism could already do and
/// which is exactly why nobody noticed the gap for so long.
/// </para>
/// <para>
/// <strong>If the control ever stops failing, this runner is dead weight — delete it, do not keep
/// it.</strong> A control that passes means the value is already accepted without migration 005,
/// which would mean something else is delivering the schema and this machinery is redundant.
/// </para>
/// </remarks>
public sealed class ObservabilitySchemaUpgradeTests
{
    private const string InsertCancelledSession =
        """
        INSERT INTO sessions (conversation_id, agent_name, status)
        VALUES ('upgrade-probe', 'ProbeAgent', 'cancelled')
        """;

    [SkippableFact]
    public async Task ADatabaseOnThePreviousRelease_RejectsCancelled_UntilTheMigrationRunnerReachesIt()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var all = ObservabilityMigrations.Load();
        var previousRelease = all.Where(s => s.Ordinal < 5).ToArray();
        Assert.Equal(4, previousRelease.Length);

        // ---- Arrange: a database as a consumer running last month's harness would have it.
        await ApplyAsync(schema, previousRelease);

        // ---- Control. This must fail, with the same instrument the treatment uses.
        var rejected = await schema.TryExecuteAsync(InsertCancelledSession);
        Assert.NotNull(rejected);
        Assert.Contains("sessions_status_check", rejected!.ToString(), StringComparison.OrdinalIgnoreCase);

        // ---- Treatment: the same database, brought forward by the runner.
        var applied = await ApplyAsync(schema, all);
        Assert.Equal(1, applied); // only 005 was outstanding

        var accepted = await schema.TryExecuteAsync(InsertCancelledSession);
        Assert.Null(accepted);

        var stored = await schema.ScalarAsync<string>(
            "SELECT status FROM sessions WHERE conversation_id = 'upgrade-probe'");
        Assert.Equal("cancelled", stored);
    }

    [SkippableFact]
    public async Task TheWidenedConstraintStillRefusesAWordThatIsNotAStatus()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        await ApplyAsync(schema, ObservabilityMigrations.Load());

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
        await ApplyAsync(schema, ObservabilityMigrations.Load());

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

        // The oldest thing that can exist: a database created by the very first schema file, with
        // none of the columns or tables added since. This is the case that used to be unreachable.
        await ApplyAsync(schema, all.Take(1).ToArray());
        Assert.Equal(0, await CountTablesAsync(schema, "context_snapshots"));

        var applied = await ApplyAsync(schema, all);

        Assert.Equal(all.Count - 1, applied);
        Assert.Equal(1, await CountTablesAsync(schema, "context_snapshots"));
        Assert.Equal(1, await schema.ScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.columns " +
            $"WHERE table_schema = '{schema.SchemaName}' AND table_name = 'session_messages' " +
            "AND column_name = 'content_full'"));
    }

    private static async Task<int> ApplyAsync(
        MigrationTestSchema schema, IReadOnlyList<MigrationScript> scripts)
    {
        var runner = new PostgresMigrationRunner(
            new PostgresMigrationOptions("schema_migrations", schema.AdvisoryLockKey),
            scripts,
            NullLogger.Instance);

        await using var connection = await schema.OpenAsync();
        return await runner.ApplyAsync(connection);
    }

    private static Task<int> CountTablesAsync(MigrationTestSchema schema, string tableName) =>
        schema.ScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.tables " +
            $"WHERE table_schema = '{schema.SchemaName}' AND table_name = '{tableName}'");
}

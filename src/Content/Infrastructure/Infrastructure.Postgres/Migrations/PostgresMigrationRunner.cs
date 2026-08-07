using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// Applies an ordered set of SQL migrations to a Postgres database, recording what it has applied so
/// that a database which already holds data receives only the changes it is missing.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the template previously had no way to change the shape of a database that was
/// already in use. The observability schema was delivered by files mounted at
/// <c>/docker-entrypoint-initdb.d</c>, which Postgres runs exactly once — when it initialises an
/// empty data directory — and never again. A consumer who had been running the harness for a month
/// could not receive a schema change at all, and CI could not see the problem because it built a
/// fresh database on every run.
/// </para>
/// <para>
/// <strong>The whole run is one transaction.</strong> Postgres applies DDL transactionally, so either
/// every outstanding migration lands or none does. That is a deliberate choice over the more common
/// per-migration transaction: a database left half-upgraded is a state nothing else in this system
/// knows how to reason about, whereas a database left at its previous version is simply the state it
/// was already in, and the throw makes it loud.
/// </para>
/// <para>
/// <strong>Failure is fatal by design.</strong> The observability store swallows and logs write
/// failures so telemetry can never fail the agent turn that produced it. That rule must not extend
/// here: a schema failure that reports success is exactly how the original defect stayed invisible
/// for as long as it did.
/// </para>
/// </remarks>
public sealed class PostgresMigrationRunner
{
    private readonly PostgresMigrationOptions _options;
    private readonly IReadOnlyList<MigrationScript> _scripts;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresMigrationRunner"/> class.
    /// </summary>
    /// <param name="options">Ledger table and advisory lock key identifying this migration set.</param>
    /// <param name="scripts">The migration set, normally from <see cref="EmbeddedSqlMigrationSource"/>.</param>
    /// <param name="logger">Logger recording which migrations were applied.</param>
    /// <exception cref="ArgumentException">The ledger table name is not a bare identifier.</exception>
    public PostgresMigrationRunner(
        PostgresMigrationOptions options,
        IReadOnlyList<MigrationScript> scripts,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;

        // Sorted here rather than trusted from the caller. EmbeddedSqlMigrationSource already returns
        // an ordered set, so this looks redundant — but "ordered" was a property of one particular
        // caller, not of the runner, and a caller that built its list by hand got whatever order it
        // happened to write. That failed exactly as you would expect and not at all when you would
        // want: migration 002 ran before 001 and died on a table 001 had not created yet.
        _scripts = scripts.OrderBy(s => s.Ordinal).ToArray();
        _logger = logger;
    }

    /// <summary>
    /// Applies every migration this database has not recorded, and returns how many ran.
    /// </summary>
    /// <param name="connection">An open connection to the target database.</param>
    /// <param name="cancellationToken">Token to cancel the run.</param>
    /// <returns>The number of migrations applied; zero when the database was already current.</returns>
    /// <exception cref="PostgresMigrationException">
    /// A migration failed. The transaction is rolled back, so the database is left at the version it
    /// held before the run.
    /// </exception>
    public async Task<int> ApplyAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize across processes before reading the ledger. Order matters: a runner that read
        // the ledger first could decide a script was unapplied, wait for the lock, and then apply it
        // a second time on top of another host's work.
        //
        // The read below is correct only because Postgres defaults to READ COMMITTED, where each
        // statement takes a fresh snapshot — so once this lock is granted, the ledger query sees the
        // rows the previous holder committed. Under REPEATABLE READ it would not, and two hosts
        // starting together would both run the full set.
        await using (var lockCommand =
            new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", _options.AdvisoryLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureLedgerAsync(connection, transaction, cancellationToken);
        var applied = await ReadAppliedAsync(connection, transaction, cancellationToken);

        var pending = _scripts.Where(s => !applied.Contains(s.Id)).ToArray();

        // No early return for the up-to-date case. It existed only to log at Debug instead of
        // Information, and it bought that with a second CommitAsync and a second exit from the one
        // block whose entire contract is "did the transaction close" — the shape where a later edit
        // most easily lands on one branch and not the other. The loop is already a no-op when
        // nothing is pending, so the commit stays in one place and the logging branches after it.
        foreach (var script in pending)
        {
            try
            {
                await using var command = new NpgsqlCommand(script.Sql, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);

                await using var record = new NpgsqlCommand(
                    $"INSERT INTO {_options.LedgerTable} (id) VALUES (@id)", connection, transaction);
                record.Parameters.AddWithValue("id", script.Id);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PostgresMigrationException(script.Id, ex);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        if (pending.Length == 0)
        {
            _logger.LogDebug(
                "Schema is current: {Total} migration(s) already applied in ledger {Ledger}.",
                _scripts.Count, _options.LedgerTable);
        }
        else
        {
            _logger.LogInformation(
                "Applied {Count} schema migration(s) to ledger {Ledger}: {Migrations}.",
                pending.Length, _options.LedgerTable, string.Join(", ", pending.Select(s => s.Id)));
        }

        return pending.Length;
    }

    private async Task EnsureLedgerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            CREATE TABLE IF NOT EXISTS {_options.LedgerTable} (
                id         TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
            """,
            connection, transaction);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<HashSet<string>> ReadAppliedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand(
            $"SELECT id FROM {_options.LedgerTable}", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            applied.Add(reader.GetString(0));

        return applied;
    }
}

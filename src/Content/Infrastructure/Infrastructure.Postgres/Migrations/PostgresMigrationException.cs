namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// Thrown when a schema migration fails. The run is transactional, so the database is left at the
/// version it held before the attempt.
/// </summary>
/// <remarks>
/// A distinct type rather than a rethrow so callers can tell "the schema could not be brought up to
/// date" apart from an ordinary query failure against a database that is already correct. The
/// migration's id is on the exception because the inner Postgres error names a table or constraint,
/// not the script that tried to change it.
/// </remarks>
public sealed class PostgresMigrationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PostgresMigrationException"/> class.</summary>
    /// <param name="migrationId">The id of the migration that failed.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PostgresMigrationException(string migrationId, Exception innerException)
        : base($"Schema migration '{migrationId}' failed; no migrations were applied.", innerException)
    {
        MigrationId = migrationId;
    }

    /// <summary>Initializes a new instance of the <see cref="PostgresMigrationException"/> class.</summary>
    /// <param name="migrationId">The id of the migration the run refused to proceed past.</param>
    /// <param name="message">Why the run was refused.</param>
    /// <remarks>
    /// For refusals decided before any SQL is sent — a script whose text no longer matches what this
    /// database recorded, for instance. There is no inner exception because nothing failed: the run
    /// was stopped on purpose.
    /// </remarks>
    public PostgresMigrationException(string migrationId, string message)
        : base(message)
    {
        MigrationId = migrationId;
    }

    /// <summary>The id of the migration that failed.</summary>
    public string MigrationId { get; }
}

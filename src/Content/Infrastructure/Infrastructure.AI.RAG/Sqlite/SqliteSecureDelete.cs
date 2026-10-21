using System.Data;
using System.Data.Common;
using Domain.Common.Compliance;
using Microsoft.Data.Sqlite;

namespace Infrastructure.AI.RAG.Sqlite;

/// <summary>
/// Makes SQLite overwrite deleted content with zeros on every connection this harness opens (#900), so an erased person's
/// text does not stay readable in the database file's free pages.
/// </summary>
/// <remarks>
/// <para>
/// SQLite normally marks a deleted row's space free without clearing it. Anyone who can read the file can then recover the
/// text, which makes a right-to-erasure deletion cosmetic. <c>PRAGMA secure_delete = ON</c> overwrites it, but it is a
/// per-connection setting, so it has to be applied every time a connection opens. Every SQLite connection in the
/// harness goes through this type: hand-written ones via <see cref="CreateConnection"/>, Entity Framework ones via the
/// interceptor that calls <see cref="Apply"/>. <c>EverySqliteConnectionScrubsDeletesTests</c> holds that in place.
/// </para>
/// <para>
/// In write-ahead-log mode (which Entity Framework sets when it creates a database) the overwritten page lands in the log
/// and the original stays in the file until a checkpoint, so erasers also call <see cref="TruncateWriteAheadLogAsync"/>.
/// </para>
/// <para>
/// Limits, also stated in the data-protection documentation: it covers deletes made from now on, so rows deleted before it
/// existed stay in free pages until a one-time <c>VACUUM</c> (and, for a full-text table, its <c>optimize</c> command
/// first); full-text tables need their own option as well (see <see cref="EnableFts5SecureDelete"/>); and a file system
/// or backup copy made before a deletion is outside its reach.
/// </para>
/// </remarks>
[PersonalDataChannel("Opens SQLite connections and runs maintenance pragmas for the stores that call it; each caller describes what its database holds.")]
public static class SqliteSecureDelete
{
    /// <summary>The statement that turns secure delete on for one connection.</summary>
    public const string Pragma = "PRAGMA secure_delete = ON;";

    /// <summary>Creates a connection that turns secure delete on every time it opens.</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <returns>An unopened connection.</returns>
    public static SqliteConnection CreateConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.StateChange += (sender, args) =>
        {
            if (args.CurrentState == ConnectionState.Open && sender is DbConnection opened)
                Apply(opened);
        };
        return connection;
    }

    /// <summary>Turns secure delete on for an open connection.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    public static void Apply(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Turns FTS5's own secure-delete option on for <paramref name="table"/>, so a deleted row's words are removed from the
    /// full-text index instead of being left behind for a later merge. The core pragma does not reach FTS5's index.
    /// </summary>
    /// <remarks>
    /// The option is stored in the table and persists. Once rows are deleted with it on, SQLite versions before 3.42 can
    /// no longer read the table; the bundled SQLite is newer.
    /// </remarks>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">The FTS5 table name, a constant owned by the caller.</param>
    /// <param name="transaction">The transaction the caller holds, if any.</param>
    public static void EnableFts5SecureDelete(SqliteConnection connection, string table, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (!table.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || char.IsAsciiDigit(table[0]))
            throw new ArgumentException("The table name must be a plain SQL identifier.", nameof(table));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table}({table}, rank) VALUES('secure-delete', 1);";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Folds a write-ahead-log database's log into the file and empties it, so neither the frames that once wrote the
    /// erased rows nor the original pages outlive an erasure. Call it after an erasure's deletes, on the same database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In write-ahead-log mode a deletion lands in the log, and the main file keeps the old page until a checkpoint;
    /// earlier log frames that wrote the row stay until they are overwritten. On a quiet host with pooled connections
    /// open, that can be a long time, so secure delete alone does not clear the bytes. A database in another journal mode
    /// reports nothing to do.
    /// </para>
    /// <para>
    /// Throws when another connection is mid-read and the log could not be emptied, so the store is reported as not
    /// erased and a retry is asked for, rather than the receipt claiming the bytes are gone.
    /// </para>
    /// </remarks>
    /// <param name="connection">An open connection to the database just erased from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The log was busy and was not emptied.</exception>
    public static async Task TruncateWriteAheadLogAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) != 0)
        {
            throw new InvalidOperationException(
                "The write-ahead log could not be emptied because another connection was using it; erased rows may remain in it.");
        }
    }
}

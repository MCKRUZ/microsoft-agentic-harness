using System.Data.Common;
using System.Globalization;

namespace Infrastructure.AI.RAG.Sqlite;

/// <summary>
/// Makes a SQLite connection wait for the database's write lock inside SQLite itself, in short steps, instead of in the
/// driver's fixed 150 ms sleeps.
/// </summary>
/// <remarks>
/// <para>
/// SQLite lets one connection write at a time. When a second writer finds the lock taken and the connection has no busy
/// timeout (the default), SQLite reports "busy" at once and <c>Microsoft.Data.Sqlite</c> retries with a blocking
/// <c>Thread.Sleep(150)</c> until the command timeout. A lock held for 10 ms therefore costs the next writer at least
/// 150 ms. Under load that sleep, not the database work, set the time a save took: at 15 workflow submissions a second
/// the median wait to start a transaction was 158 ms, while every statement inside it took under 1 ms.
/// </para>
/// <para>
/// <c>PRAGMA busy_timeout</c> hands the wait to SQLite's own busy handler, which retries after 1, 2, 5, 10 ms and so on,
/// so a writer starts soon after the lock frees. The timeout matches the driver's default 30-second command timeout, so
/// the longest a writer can wait for the lock is unchanged; only how quickly it notices the lock is free changes.
/// </para>
/// <para>
/// Applied wherever <see cref="SqliteSecureDelete"/> is, which is every SQLite connection the harness opens
/// (<c>EverySqliteConnectionScrubsDeletesTests</c> holds both in place). A caller that must not wait, such as the
/// write-ahead-log truncation after an erasure, turns it off around its own command with <see cref="SuspendAsync"/>.
/// </para>
/// </remarks>
public static class SqliteLockWait
{
    /// <summary>How long a connection waits for the write lock before SQLite reports the database busy.</summary>
    public const int TimeoutMilliseconds = 30_000;

    /// <summary>The statement that sets the wait for one connection.</summary>
    public const string Pragma = "PRAGMA busy_timeout = 30000;";

    /// <summary>Sets the lock wait on an open connection.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    public static void Apply(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Turns the lock wait off on an open connection until the returned handle is disposed, then restores the wait the
    /// connection had before.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle whose disposal restores the previous wait.</returns>
    public static async Task<IAsyncDisposable> SuspendAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var previous = await ReadAsync(connection, cancellationToken);
        await SetAsync(connection, 0, cancellationToken);
        return new Restore(connection, previous);
    }

    /// <summary>Reads the lock wait currently set on an open connection, in milliseconds.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The wait in milliseconds; 0 means busy is reported at once.</returns>
    public static async Task<int> ReadAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task SetAsync(DbConnection connection, int milliseconds, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {milliseconds};");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class Restore(DbConnection connection, int previous) : IAsyncDisposable
    {
        // Not cancellable: a restore skipped because the caller's request was cancelled would leave a pooled connection
        // reporting busy at once for whoever uses it next.
        public async ValueTask DisposeAsync() => await SetAsync(connection, previous, CancellationToken.None);
    }
}

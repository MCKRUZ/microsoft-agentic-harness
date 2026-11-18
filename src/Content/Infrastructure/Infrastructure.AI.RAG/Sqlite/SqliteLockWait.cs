using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

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
/// so a writer starts soon after the lock frees. The wait is the connection's own default command timeout
/// (<c>Default Timeout</c> in the connection string, 30 seconds unless set), so a consumer who shortens it to fail fast
/// still fails fast. The driver checks that timeout only between statements, so one command of several statements can
/// wait up to the timeout on each; a single statement waits no longer than before.
/// </para>
/// <para>
/// Applied by <see cref="SqliteSecureDelete.ConfigureOpened"/>, which runs on every SQLite connection the harness opens
/// (<c>EverySqliteConnectionScrubsDeletesTests</c> holds that in place). A caller that must not wait, such as a
/// write-ahead-log checkpoint, turns it off around its own command with <see cref="SuspendAsync"/>.
/// </para>
/// </remarks>
public static class SqliteLockWait
{
    /// <summary>The wait a connection gets when its connection string sets no timeout: the driver's 30-second default.</summary>
    public const int DefaultTimeoutMilliseconds = 30_000;

    /// <summary>Sets the lock wait on an open connection to its default command timeout.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    public static void Apply(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = SetStatement(TimeoutFor(connection));
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
        await ExecuteAsync(connection, SetStatement(0), cancellationToken);
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

    private static int TimeoutFor(DbConnection connection) =>
        connection is SqliteConnection sqlite ? sqlite.DefaultTimeout * 1000 : DefaultTimeoutMilliseconds;

    private static string SetStatement(int milliseconds) =>
        string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {milliseconds};");

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class Restore(DbConnection connection, int previous) : IAsyncDisposable
    {
        // Not cancellable: a long-lived connection (the graph backend holds one for its lifetime) is never reopened, so a
        // skipped restore would leave it reporting busy at once, back on the driver's 150 ms sleeps, until it closes.
        // Skipped when the connection is no longer open, so a failure that closed it is not masked by this one.
        public async ValueTask DisposeAsync()
        {
            if (connection.State == ConnectionState.Open)
                await ExecuteAsync(connection, SetStatement(previous), CancellationToken.None);
        }
    }
}

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
/// (<c>Default Timeout</c> in the connection string, 30 seconds unless set), so a consumer who shortens that to fail fast
/// still fails fast. A timeout set per command instead (for example Entity Framework's <c>CommandTimeout</c>) is not
/// seen here: the busy wait still follows <c>Default Timeout</c>, so set both when shortening. The driver checks its
/// timeout only between statements, so one command of several statements can wait up to the timeout on each.
/// </para>
/// <para>
/// SQLite's wait runs inside one native call, so a cancellation token is not seen until it ends; the driver's own loop
/// checked it between sleeps. A request cancelled while another writer holds the lock can therefore take up to the
/// timeout to stop, and with <c>Default Timeout=0</c> it waits for the lock however long that takes.
/// </para>
/// <para>
/// Set by <see cref="SqliteSecureDelete.ConfigureOpened"/>, which runs on every SQLite connection the harness opens
/// (<c>EverySqliteConnectionScrubsDeletesTests</c> holds that in place). A write-ahead-log checkpoint must not wait, so it
/// runs <see cref="NoWaitPragma"/> first.
/// </para>
/// </remarks>
public static class SqliteLockWait
{
    /// <summary>The statement that turns the lock wait off, so busy is reported at once.</summary>
    public const string NoWaitPragma = "PRAGMA busy_timeout = 0;";

    /// <summary>The statement that sets <paramref name="connection"/>'s lock wait to its default command timeout.</summary>
    /// <param name="connection">A SQLite connection.</param>
    /// <returns>The <c>PRAGMA busy_timeout</c> statement for this connection.</returns>
    public static string PragmaFor(DbConnection connection) =>
        string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {TimeoutMillisecondsFor(connection)};");

    /// <summary>
    /// The lock wait, in milliseconds, that matches how the driver reads <paramref name="connection"/>'s default command
    /// timeout. The driver reads 0 as "no limit", so it maps to the longest wait SQLite accepts rather than to 0, which
    /// would turn the wait off; it gives up at the first busy on a negative value, so that maps to no wait.
    /// </summary>
    /// <param name="connection">A SQLite connection, or a wrapper around one (a profiler, for instance), whose timeout is
    /// then read from its connection string.</param>
    /// <returns>The value for <c>PRAGMA busy_timeout</c>.</returns>
    public static int TimeoutMillisecondsFor(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var seconds = connection is SqliteConnection sqlite
            ? sqlite.DefaultTimeout
            : new SqliteConnectionStringBuilder(connection.ConnectionString).DefaultTimeout;
        return seconds switch
        {
            0 => int.MaxValue,
            < 0 => 0,
            _ => (int)Math.Min(seconds * 1000L, int.MaxValue),
        };
    }
}

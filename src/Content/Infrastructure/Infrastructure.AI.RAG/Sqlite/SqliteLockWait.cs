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
    public static string PragmaFor(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var milliseconds = ((SqliteConnection)connection).DefaultTimeout * 1000;
        return string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {milliseconds};");
    }
}

using System.Diagnostics;
using FluentAssertions;
using Infrastructure.AI.RAG.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Sqlite;

/// <summary>
/// <see cref="SqliteLockWait"/>: every connection the harness makes waits for SQLite's write lock inside SQLite, and the
/// one caller that must not wait, the write-ahead-log truncation after an erasure, still fails at once.
/// </summary>
public sealed class SqliteLockWaitTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lock-wait-" + Guid.NewGuid().ToString("N"));

    public SqliteLockWaitTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a pooled handle on Windows; it is in the temp folder.
        }
    }

    [Fact]
    public void CreateConnection_SetsTheLockWaitEveryTimeItOpens()
    {
        // Without it, a writer that finds the lock taken sleeps 150 ms in the driver before looking again, which is what
        // made saves slow under load. The setting is per connection, so every open must apply it.
        using var connection = SqliteSecureDelete.CreateConnection($"Data Source={Path.Combine(_directory, "a.db")}");

        connection.Open();
        BusyTimeout(connection).Should().Be(SqliteLockWait.TimeoutMilliseconds);
        connection.Close();
        connection.Open();
        BusyTimeout(connection).Should().Be(SqliteLockWait.TimeoutMilliseconds);
    }

    [Fact]
    public async Task TruncateWriteAheadLog_ReaderHoldsTheLog_FailsAtOnceAndRestoresTheLockWait()
    {
        // An erasure that cannot empty the log must say so and ask for a retry straight away. With the lock wait left on
        // the checkpoint would sit in SQLite's busy handler for 30 seconds before reporting the same thing.
        var file = Path.Combine(_directory, "busy.db");
        using var writer = SqliteSecureDelete.CreateConnection($"Data Source={file};Pooling=False");
        writer.Open();
        Execute(writer, "PRAGMA journal_mode=WAL; CREATE TABLE t(v TEXT); INSERT INTO t VALUES('x'), ('y');");

        using var reader = new SqliteConnection($"Data Source={file};Pooling=False");
        reader.Open();
        using var readTransaction = reader.BeginTransaction(deferred: true);
        using var read = reader.CreateCommand();
        read.Transaction = readTransaction;
        read.CommandText = "SELECT v FROM t";
        using var rows = read.ExecuteReader();
        rows.Read().Should().BeTrue("the reader must hold a snapshot while the log is truncated");
        Execute(writer, "DELETE FROM t;");

        var clock = Stopwatch.StartNew();
        var emptied = await SqliteSecureDelete.TryTruncateWriteAheadLogAsync(writer, CancellationToken.None);
        clock.Stop();

        emptied.Should().BeFalse();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a busy log is reported at once, not after the lock wait");
        BusyTimeout(writer).Should().Be(SqliteLockWait.TimeoutMilliseconds,
            "the connection goes back to waiting for the lock once the checkpoint is done");
    }

    [Fact]
    public async Task SuspendAsync_TurnsTheWaitOffThenRestoresWhateverWasSetBefore()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "b.db")}");
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 1234;");

        await using (await SqliteLockWait.SuspendAsync(connection, CancellationToken.None))
        {
            BusyTimeout(connection).Should().Be(0);
        }

        BusyTimeout(connection).Should().Be(1234, "the previous value is restored, not the harness default");
    }

    private static long BusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

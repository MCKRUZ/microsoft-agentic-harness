using System.Diagnostics;
using FluentAssertions;
using Infrastructure.AI.RAG.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Sqlite;

/// <summary>
/// <see cref="SqliteLockWait"/>: every connection the harness makes waits for SQLite's write lock inside SQLite, and the
/// one kind of command that must not wait, a write-ahead-log checkpoint after an erasure, still fails at once.
/// </summary>
public sealed class SqliteLockWaitTests : IDisposable
{
    /// <summary>Microsoft.Data.Sqlite's default command timeout, which the lock wait follows when nothing is configured.</summary>
    private const int DriverDefaultTimeoutMilliseconds = 30_000;

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
        // Pooling off, so the reopen gets a fresh native handle that remembers nothing from the first open.
        using var connection = SqliteSecureDelete.CreateConnection($"Data Source={Path.Combine(_directory, "a.db")};Pooling=False");

        connection.Open();
        BusyTimeout(connection).Should().Be(DriverDefaultTimeoutMilliseconds);
        connection.Close();
        connection.Open();
        BusyTimeout(connection).Should().Be(DriverDefaultTimeoutMilliseconds);
    }

    [Fact]
    public void CreateConnection_HonoursAConfiguredTimeout()
    {
        // A consumer who shortens the timeout so contended writes fail fast must not have it lengthened to 30 seconds.
        using var connection = SqliteSecureDelete.CreateConnection(
            $"Data Source={Path.Combine(_directory, "c.db")};Default Timeout=5");

        connection.Open();

        BusyTimeout(connection).Should().Be(5000);
    }

    [Fact]
    public void CreateConnection_NoTimeoutConfigured_WaitsWithoutLimitRatherThanNotAtAll()
    {
        // The driver reads Default Timeout=0 as "retry forever". Copied as busy_timeout = 0 it would turn the wait off and
        // put the connection back on the 150 ms sleeps this exists to remove.
        using var connection = SqliteSecureDelete.CreateConnection(
            $"Data Source={Path.Combine(_directory, "d.db")};Default Timeout=0");

        connection.Open();

        BusyTimeout(connection).Should().Be(int.MaxValue);
    }

    [Fact]
    public async Task TruncateWriteAheadLog_ReaderHoldsTheLog_FailsAtOnceAndRestoresTheLockWait()
    {
        // An erasure that cannot empty the log must say so and ask for a retry straight away. With the lock wait left on
        // the checkpoint would sit in SQLite's busy handler for 30 seconds before reporting the same thing.
        var file = Path.Combine(_directory, "busy.db");
        // A non-default timeout, so the restore is shown to put back this connection's own wait, not a fixed 30 seconds.
        using var writer = SqliteSecureDelete.CreateConnection($"Data Source={file};Pooling=False;Default Timeout=7");
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
        BusyTimeout(writer).Should().Be(7000,
            "the connection goes back to waiting for the lock once the checkpoint is done");
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

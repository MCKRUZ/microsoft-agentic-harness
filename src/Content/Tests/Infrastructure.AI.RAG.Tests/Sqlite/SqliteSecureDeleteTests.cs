using FluentAssertions;
using Infrastructure.AI.RAG.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Sqlite;

/// <summary>
/// <see cref="SqliteSecureDelete"/> (#900): connections it makes overwrite deleted rows, the write-ahead log can be
/// emptied after an erasure and says so when it cannot, and the full-text option takes only a plain table name.
/// </summary>
public sealed class SqliteSecureDeleteTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "secure-delete-" + Guid.NewGuid().ToString("N"));

    public SqliteSecureDeleteTests() => Directory.CreateDirectory(_directory);

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
    public void CreateConnection_TurnsSecureDeleteOnEveryTimeItOpens()
    {
        using var connection = SqliteSecureDelete.CreateConnection($"Data Source={Path.Combine(_directory, "a.db")}");

        connection.Open();
        Pragma(connection, "secure_delete").Should().Be(1);
        connection.Close();
        connection.Open();
        Pragma(connection, "secure_delete").Should().Be(1, "the setting is per connection, so every open must apply it");
    }

    [Fact]
    public async Task TruncateWriteAheadLog_AfterADeleteInWalMode_EmptiesTheLog()
    {
        var file = Path.Combine(_directory, "wal.db");
        using var connection = SqliteSecureDelete.CreateConnection($"Data Source={file};Pooling=False");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL; CREATE TABLE t(v TEXT); INSERT INTO t VALUES('x'); DELETE FROM t;");
        new FileInfo(file + "-wal").Length.Should().BeGreaterThan(0, "the seed must leave frames in the log");

        await SqliteSecureDelete.TruncateWriteAheadLogAsync(connection, CancellationToken.None);

        new FileInfo(file + "-wal").Length.Should().Be(0);
    }

    [Fact]
    public async Task TruncateWriteAheadLog_AnotherConnectionMidRead_ThrowsRatherThanClaimingSuccess()
    {
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

        var act = () => SqliteSecureDelete.TruncateWriteAheadLogAsync(writer, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("chunks_fts; DROP TABLE x")]
    [InlineData("1table")]
    [InlineData("name with space")]
    public void EnableFts5SecureDelete_NotAPlainIdentifier_IsRefused(string table)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var act = () => SqliteSecureDelete.EnableFts5SecureDelete(connection, table);

        act.Should().Throw<ArgumentException>();
    }

    private static long Pragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

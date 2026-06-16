using System.Data.Common;
using Application.Common.Interfaces.Data;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.SqlDatabase;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.SqlDatabase;

/// <summary>
/// Regression coverage for the solution-review finding that the read-only SQL guard could be
/// bypassed with <c>SELECT ... INTO</c> (and <c>MERGE</c>): those statements start with SELECT,
/// contain no previously-blocked keyword, and are single statements — yet they write to the
/// database. The fix adds <c>INTO</c> and <c>MERGE</c> to the mutation blocklist.
/// </summary>
public sealed class SafeSqlQueryExecutorSolutionReviewFixTests : IDisposable
{
    private const string SharedConnectionString =
        "Data Source=SafeSqlQueryExecutorSolutionReviewFixTests;Mode=Memory;Cache=Shared";

    private readonly SqliteConnection _keepAlive;

    public SafeSqlQueryExecutorSolutionReviewFixTests()
    {
        _keepAlive = new SqliteConnection(SharedConnectionString);
        _keepAlive.Open();

        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE secrets (id INTEGER PRIMARY KEY, value TEXT);
            INSERT INTO secrets VALUES (1, 'classified');
        """;
        cmd.ExecuteNonQuery();
    }

    private sealed class SqliteConnectionFactory : ISqlConnectionFactory
    {
        public DbConnection CreateConnection() => new SqliteConnection(SharedConnectionString);
    }

    [Theory]
    [InlineData("SELECT * INTO exfil FROM secrets")]
    [InlineData("SELECT id, value INTO dbo.exfil FROM secrets")]
    [InlineData("MERGE secrets AS t USING secrets AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.value = s.value")]
    public async Task ExecuteAsync_SelectIntoOrMerge_ThrowsInvalidOperationException(string sql)
    {
        var sut = CreateExecutor();

        var act = () => sut.ExecuteAsync(sql, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*read-only*");
    }

    private SafeSqlQueryExecutor CreateExecutor()
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.SqlDatabase = new SqlDatabaseConfig
        {
            MaxRows = 100,
            QueryTimeoutSeconds = 5
        };
        var configMock = new Mock<IOptionsMonitor<AppConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(appConfig);

        return new SafeSqlQueryExecutor(
            new SqliteConnectionFactory(),
            configMock.Object,
            NullLogger<SafeSqlQueryExecutor>.Instance);
    }

    public void Dispose() => _keepAlive.Dispose();
}

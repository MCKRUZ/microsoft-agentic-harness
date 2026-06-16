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

public sealed class SafeSqlQueryExecutorTests : IDisposable
{
    // A shared-cache in-memory database backed by a single keep-alive connection so the
    // per-call connection factory can hand out fresh connections that observe the same data.
    private const string SharedConnectionString =
        "Data Source=SafeSqlQueryExecutorTests;Mode=Memory;Cache=Shared";

    private readonly SqliteConnection _keepAlive;

    public SafeSqlQueryExecutorTests()
    {
        _keepAlive = new SqliteConnection(SharedConnectionString);
        _keepAlive.Open();

        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE products (id INTEGER PRIMARY KEY, name TEXT, price REAL);
            INSERT INTO products VALUES (1, 'Widget', 9.99);
            INSERT INTO products VALUES (2, 'Gadget', 19.99);
            INSERT INTO products VALUES (3, 'Doohickey', 4.99);
        """;
        cmd.ExecuteNonQuery();
    }

    private sealed class SqliteConnectionFactory : ISqlConnectionFactory
    {
        public DbConnection CreateConnection() => new SqliteConnection(SharedConnectionString);
    }

    [Fact]
    public async Task ExecuteAsync_ValidSelect_ReturnsRows()
    {
        var sut = CreateExecutor();

        var result = await sut.ExecuteAsync("SELECT name, price FROM products", null, CancellationToken.None);

        result.Rows.Should().HaveCount(3);
        result.WasTemplateMatch.Should().BeFalse();
        result.Rows[0]["name"].Should().Be("Widget");
    }

    [Theory]
    [InlineData("INSERT INTO products VALUES (4, 'Bad', 0)")]
    [InlineData("UPDATE products SET price = 0")]
    [InlineData("DELETE FROM products WHERE id = 1")]
    [InlineData("DROP TABLE products")]
    [InlineData("ALTER TABLE products ADD COLUMN evil TEXT")]
    [InlineData("TRUNCATE TABLE products")]
    public async Task ExecuteAsync_MutationSql_ThrowsInvalidOperationException(string sql)
    {
        var sut = CreateExecutor();

        var act = () => sut.ExecuteAsync(sql, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*read-only*");
    }

    [Fact]
    public async Task ExecuteAsync_RespectsRowLimit()
    {
        var sut = CreateExecutor(maxRows: 2);

        var result = await sut.ExecuteAsync("SELECT * FROM products", null, CancellationToken.None);

        result.Rows.Should().HaveCount(2, "row limit should cap results");
    }

    [Fact]
    public async Task ExecuteAsync_WithParameters_BindsCorrectly()
    {
        var sut = CreateExecutor();
        var parameters = new Dictionary<string, object?> { ["minPrice"] = 5.0 };

        var result = await sut.ExecuteAsync(
            "SELECT name FROM products WHERE price > @minPrice", parameters, CancellationToken.None);

        result.Rows.Should().HaveCount(2);
    }

    private SafeSqlQueryExecutor CreateExecutor(int maxRows = 100)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.SqlDatabase = new SqlDatabaseConfig
        {
            MaxRows = maxRows,
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

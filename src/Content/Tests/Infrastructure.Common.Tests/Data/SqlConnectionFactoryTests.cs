using System.Data.Common;
using Application.Common.Interfaces.Data;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.Common.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.Common.Tests.Data;

public sealed class SqlConnectionFactoryTests
{
    [Fact]
    public void CreateConnection_ReturnsConnectionWithCorrectConnectionString()
    {
        // Arrange
        var appConfig = BuildAppConfig("Data Source=:memory:");
        var configMock = new Mock<IOptionsMonitor<AppConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(appConfig);

        var sut = new SqlConnectionFactory(SqliteFactory.Instance, configMock.Object);

        // Act
        var connection = sut.CreateConnection();

        // Assert
        connection.Should().NotBeNull();
        connection.ConnectionString.Should().Be("Data Source=:memory:");
        connection.Should().BeAssignableTo<DbConnection>();
    }

    [Fact]
    public void CreateConnection_ReturnsUnopenedConnection()
    {
        // Arrange — verifies callers own connection lifetime (contract from ISqlConnectionFactory docs)
        var appConfig = BuildAppConfig("Data Source=:memory:");
        var configMock = new Mock<IOptionsMonitor<AppConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(appConfig);

        var sut = new SqlConnectionFactory(SqliteFactory.Instance, configMock.Object);

        // Act
        using var connection = sut.CreateConnection();

        // Assert
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public void CreateConnection_EachCallReturnsDistinctInstance()
    {
        // Arrange — each call must return a new connection so callers manage lifetimes independently
        var appConfig = BuildAppConfig("Data Source=:memory:");
        var configMock = new Mock<IOptionsMonitor<AppConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(appConfig);

        var sut = new SqlConnectionFactory(SqliteFactory.Instance, configMock.Object);

        // Act
        using var first = sut.CreateConnection();
        using var second = sut.CreateConnection();

        // Assert
        first.Should().NotBeSameAs(second);
    }

    private static AppConfig BuildAppConfig(string connectionString) =>
        new()
        {
            AI = new()
            {
                Rag = new()
                {
                    SqlDatabase = new SqlDatabaseConfig
                    {
                        ConnectionString = connectionString
                    }
                }
            }
        };
}

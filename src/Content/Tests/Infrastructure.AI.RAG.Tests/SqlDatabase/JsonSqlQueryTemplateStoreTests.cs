using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.SqlDatabase;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.SqlDatabase;

public sealed class JsonSqlQueryTemplateStoreTests
{
    [Fact]
    public async Task GetTemplatesAsync_LoadsFromJsonFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var json = """
            [
                {
                    "Name": "orders_by_date",
                    "Description": "Retrieve orders within a date range",
                    "SqlTemplate": "SELECT * FROM orders WHERE date >= @startDate AND date <= @endDate",
                    "Parameters": ["startDate", "endDate"]
                }
            ]
            """;
            await File.WriteAllTextAsync(tempFile, json);

            var config = CreateConfig(tempFile);
            var sut = new JsonSqlQueryTemplateStore(config);

            var templates = await sut.GetTemplatesAsync(CancellationToken.None);

            templates.Should().HaveCount(1);
            templates[0].Name.Should().Be("orders_by_date");
            templates[0].Parameters.Should().Contain("startDate");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetTemplatesAsync_MissingFile_ReturnsEmpty()
    {
        var config = CreateConfig("/nonexistent/path.json");
        var sut = new JsonSqlQueryTemplateStore(config);

        var templates = await sut.GetTemplatesAsync(CancellationToken.None);

        templates.Should().BeEmpty();
    }

    private static IOptionsMonitor<AppConfig> CreateConfig(string templatesPath)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.SqlDatabase = new SqlDatabaseConfig { TemplatesPath = templatesPath };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}

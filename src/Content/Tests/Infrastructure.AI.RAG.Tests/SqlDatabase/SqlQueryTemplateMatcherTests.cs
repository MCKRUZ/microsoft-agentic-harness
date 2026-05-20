using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.SqlDatabase;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.SqlDatabase;

public sealed class SqlQueryTemplateMatcherTests
{
    private readonly Mock<IChatClient> _chatClient = new();

    [Fact]
    public async Task MatchAsync_HighConfidence_ReturnsTemplateWithParameters()
    {
        var templates = new List<SqlQueryTemplate>
        {
            new()
            {
                Name = "orders_by_date",
                Description = "Retrieve orders within a date range",
                SqlTemplate = "SELECT * FROM orders WHERE date >= @startDate AND date <= @endDate",
                Parameters = ["startDate", "endDate"]
            }
        };

        var llmResponse = """{"templateName":"orders_by_date","confidence":0.9,"parameters":{"startDate":"2026-01-01","endDate":"2026-12-31"}}""";
        SetupChatResponse(llmResponse);

        var config = CreateConfig(0.7);
        var sut = new SqlQueryTemplateMatcher(_chatClient.Object, config);

        var result = await sut.MatchAsync("Show me orders from 2026", templates, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Template.Name.Should().Be("orders_by_date");
        result.Value.Parameters["startDate"].Should().Be("2026-01-01");
    }

    [Fact]
    public async Task MatchAsync_LowConfidence_ReturnsNull()
    {
        var templates = new List<SqlQueryTemplate>
        {
            new()
            {
                Name = "orders_by_date",
                Description = "Retrieve orders within a date range",
                SqlTemplate = "SELECT * FROM orders WHERE date >= @startDate",
                Parameters = ["startDate"]
            }
        };

        var llmResponse = """{"templateName":"orders_by_date","confidence":0.3,"parameters":{"startDate":"unknown"}}""";
        SetupChatResponse(llmResponse);

        var config = CreateConfig(0.7);
        var sut = new SqlQueryTemplateMatcher(_chatClient.Object, config);

        var result = await sut.MatchAsync("What is the weather?", templates, CancellationToken.None);

        result.Should().BeNull();
    }

    private void SetupChatResponse(string content)
    {
        var chatMessage = new ChatMessage(ChatRole.Assistant, content);
        var chatResponse = new ChatResponse(chatMessage);
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);
    }

    private static IOptionsMonitor<AppConfig> CreateConfig(double threshold)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.SqlDatabase = new SqlDatabaseConfig
        {
            TemplateMatchConfidenceThreshold = threshold
        };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}

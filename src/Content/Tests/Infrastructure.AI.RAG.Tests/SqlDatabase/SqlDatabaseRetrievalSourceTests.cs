using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.SqlDatabase;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.SqlDatabase;

public sealed class SqlDatabaseRetrievalSourceTests
{
    private readonly Mock<ISqlQueryTemplateStore> _templateStore = new();
    private readonly Mock<ISqlQueryExecutor> _executor = new();
    private readonly Mock<IChatClient> _chatClient = new();

    [Fact]
    public async Task RetrieveAsync_TemplateMatch_ExecutesTemplateAndConvertsToResults()
    {
        var template = new SqlQueryTemplate
        {
            Name = "user_by_name",
            Description = "Find user by name",
            SqlTemplate = "SELECT * FROM users WHERE name = @name",
            Parameters = ["name"]
        };
        _templateStore
            .Setup(s => s.GetTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([template]);

        _executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlRetrievalResult
            {
                Query = "SELECT * FROM users WHERE name = @name",
                WasTemplateMatch = true,
                Rows = [new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Alice" }]
            });

        var matcherResponse = """{"templateName":"user_by_name","confidence":0.9,"parameters":{"name":"Alice"}}""";
        SetupChatResponse(matcherResponse);

        var sut = CreateSut(allowLlmFallback: true);

        var result = await sut.RetrieveAsync("Find user Alice", 10, QueryComplexity.Moderate, CancellationToken.None);

        result.SourceName.Should().Be("sql_database");
        result.Results.Should().HaveCount(1);
        result.Results[0].Chunk.Content.Should().Contain("Alice");
    }

    [Fact]
    public async Task RetrieveAsync_NoTemplateMatch_FallsBackToLlm()
    {
        _templateStore
            .Setup(s => s.GetTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var fallbackSql = "SELECT * FROM products WHERE price < 10";
        SetupChatResponse(fallbackSql);

        _executor
            .Setup(e => e.ExecuteAsync(fallbackSql, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlRetrievalResult
            {
                Query = fallbackSql,
                WasTemplateMatch = false,
                Rows = [new Dictionary<string, object?> { ["name"] = "Widget", ["price"] = 9.99 }]
            });

        var sut = CreateSut(allowLlmFallback: true);

        var result = await sut.RetrieveAsync("Cheap products", 10, QueryComplexity.Moderate, CancellationToken.None);

        result.Results.Should().HaveCount(1);
    }

    [Fact]
    public async Task RetrieveAsync_LlmFallbackDisabled_ReturnsEmpty()
    {
        _templateStore
            .Setup(s => s.GetTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = CreateSut(allowLlmFallback: false);

        var result = await sut.RetrieveAsync("Cheap products", 10, QueryComplexity.Moderate, CancellationToken.None);

        result.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_LlmFallbackReturnsNull_ReturnsEmpty()
    {
        _templateStore
            .Setup(s => s.GetTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var chatMessage = new ChatMessage(ChatRole.Assistant, "");
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(chatMessage));

        var sut = CreateSut(allowLlmFallback: true);

        var result = await sut.RetrieveAsync("Unknown query", 10, QueryComplexity.Moderate, CancellationToken.None);

        result.Results.Should().BeEmpty();
    }

    [Fact]
    public void SourceName_IsSqlDatabase()
    {
        var sut = CreateSut(allowLlmFallback: true);
        sut.SourceName.Should().Be("sql_database");
    }

    private SqlDatabaseRetrievalSource CreateSut(bool allowLlmFallback)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.SqlDatabase = new SqlDatabaseConfig
        {
            AllowLlmFallback = allowLlmFallback,
            TemplateMatchConfidenceThreshold = 0.7
        };
        var configMock = new Mock<IOptionsMonitor<AppConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(appConfig);

        return new SqlDatabaseRetrievalSource(
            _templateStore.Object,
            _executor.Object,
            new SqlQueryTemplateMatcher(_chatClient.Object, configMock.Object),
            new TextToSqlGenerator(_chatClient.Object),
            configMock.Object,
            NullLogger<SqlDatabaseRetrievalSource>.Instance);
    }

    private void SetupChatResponse(string content)
    {
        var chatMessage = new ChatMessage(ChatRole.Assistant, content);
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(chatMessage));
    }
}

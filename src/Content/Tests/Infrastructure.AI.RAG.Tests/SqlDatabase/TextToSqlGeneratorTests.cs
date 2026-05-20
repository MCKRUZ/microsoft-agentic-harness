using FluentAssertions;
using Infrastructure.AI.RAG.SqlDatabase;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.SqlDatabase;

public sealed class TextToSqlGeneratorTests
{
    private readonly Mock<IChatClient> _chatClient = new();

    [Fact]
    public async Task GenerateAsync_ReturnsSelectStatement()
    {
        var llmResponse = "SELECT name, email FROM users WHERE active = 1";
        SetupChatResponse(llmResponse);

        var sut = new TextToSqlGenerator(_chatClient.Object);
        var schema = "CREATE TABLE users (id INT, name TEXT, email TEXT, active INT)";

        var sql = await sut.GenerateAsync("Show me all active users", schema, CancellationToken.None);

        sql.Should().NotBeNullOrEmpty();
        sql.Should().StartWith("SELECT", "generated SQL must be a SELECT statement");
    }

    [Fact]
    public async Task GenerateAsync_LlmReturnsMutation_ReturnsNull()
    {
        var llmResponse = "DELETE FROM users WHERE active = 0";
        SetupChatResponse(llmResponse);

        var sut = new TextToSqlGenerator(_chatClient.Object);

        var sql = await sut.GenerateAsync("Delete inactive users", "schema", CancellationToken.None);

        sql.Should().BeNull("mutations must be rejected at generation time");
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

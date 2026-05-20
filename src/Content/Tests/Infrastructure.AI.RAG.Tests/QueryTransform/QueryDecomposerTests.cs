using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.QueryTransform;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.QueryTransform;

public sealed class QueryDecomposerTests
{
    private readonly Mock<IRagModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockChatClient = new();

    public QueryDecomposerTests()
    {
        _mockRouter
            .Setup(r => r.GetClientForOperation("query_decomposition"))
            .Returns(_mockChatClient.Object);
    }

    private QueryDecomposer CreateDecomposer()
        => new(
            _mockRouter.Object,
            Mock.Of<ILogger<QueryDecomposer>>());

    private void SetupChatResponse(string jsonResponse)
    {
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, jsonResponse)));
    }

    [Fact]
    public async Task DecomposeAsync_ComplexMultiPartQuery_ReturnsOrderedSubQueries()
    {
        SetupChatResponse("""
            {
                "sub_queries": [
                    {"text": "What chunking strategies are available?", "order": 1, "depends_on": []},
                    {"text": "How does RAPTOR summarization work?", "order": 2, "depends_on": []},
                    {"text": "How do chunking strategies interact with RAPTOR?", "order": 3, "depends_on": [1, 2]}
                ]
            }
            """);
        var decomposer = CreateDecomposer();

        var result = await decomposer.DecomposeAsync(
            "How do the chunking strategies interact with RAPTOR summarization in the ingestion pipeline?");

        result.SubQueries.Should().HaveCount(3);
        result.SubQueries[0].Order.Should().Be(1);
        result.SubQueries[1].Order.Should().Be(2);
        result.SubQueries[2].Order.Should().Be(3);
        result.OriginalQuery.Should().Contain("chunking strategies");
    }

    [Fact]
    public async Task DecomposeAsync_SimpleQuery_ReturnsSingleSubQuery()
    {
        SetupChatResponse("""
            {
                "sub_queries": [
                    {"text": "What is the default topK value?", "order": 1, "depends_on": []}
                ]
            }
            """);
        var decomposer = CreateDecomposer();

        var result = await decomposer.DecomposeAsync("What is the default topK value?");

        result.SubQueries.Should().HaveCount(1);
        result.SubQueries[0].Text.Should().Be("What is the default topK value?");
        result.SubQueries[0].DependsOnOrders.Should().BeEmpty();
        result.RequiresSequentialExecution.Should().BeFalse();
    }

    [Fact]
    public async Task DecomposeAsync_QueryWithDependencies_SetsDependsOnOrders()
    {
        SetupChatResponse("""
            {
                "sub_queries": [
                    {"text": "What is the architecture?", "order": 1, "depends_on": []},
                    {"text": "What are the deployment requirements?", "order": 2, "depends_on": [1]}
                ]
            }
            """);
        var decomposer = CreateDecomposer();

        var result = await decomposer.DecomposeAsync(
            "Based on the architecture, what are the deployment requirements?");

        result.SubQueries[1].DependsOnOrders.Should().Contain(1);
    }

    [Fact]
    public async Task DecomposeAsync_SetsRequiresSequentialExecution_WhenDependenciesExist()
    {
        SetupChatResponse("""
            {
                "sub_queries": [
                    {"text": "Part A", "order": 1, "depends_on": []},
                    {"text": "Part B depends on A", "order": 2, "depends_on": [1]}
                ]
            }
            """);
        var decomposer = CreateDecomposer();

        var result = await decomposer.DecomposeAsync("Query with dependencies");

        result.RequiresSequentialExecution.Should().BeTrue();
    }

    [Fact]
    public async Task DecomposeAsync_EmptyQuery_ThrowsArgumentException()
    {
        var decomposer = CreateDecomposer();

        var act = () => decomposer.DecomposeAsync("");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DecomposeAsync_LlmReturnsInvalidJson_ReturnsSingleSubQueryFallback()
    {
        SetupChatResponse("I cannot decompose this query into structured JSON.");
        var decomposer = CreateDecomposer();

        var result = await decomposer.DecomposeAsync("Some complex query");

        result.SubQueries.Should().HaveCount(1);
        result.SubQueries[0].Text.Should().Be("Some complex query");
        result.SubQueries[0].Order.Should().Be(1);
        result.SubQueries[0].DependsOnOrders.Should().BeEmpty();
        result.RequiresSequentialExecution.Should().BeFalse();
    }

    [Fact]
    public async Task DecomposeAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var decomposer = CreateDecomposer();

        var act = () => decomposer.DecomposeAsync("test query", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

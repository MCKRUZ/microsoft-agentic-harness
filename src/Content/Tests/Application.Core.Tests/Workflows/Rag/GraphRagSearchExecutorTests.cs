using Application.AI.Common.Interfaces.RAG;
using Application.Core.Workflows.Rag;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.Workflows.Rag;

/// <summary>
/// Verifies <see cref="GraphRagSearchExecutor"/> behavior with and without an
/// <see cref="IGraphRagService"/>. The service is registered only while the graph database
/// backend is enabled, so the executor must degrade the GraphRag branch with an explanatory
/// context — never an exception — when it is absent.
/// </summary>
public sealed class GraphRagSearchExecutorTests
{
    private static ClassifiedQuery Query() =>
        new("What are the main themes?", TopK: 5, CollectionName: null, RetrievalStrategy.GraphRag);

    [Fact]
    public async Task HandleAsync_ServiceAbsent_ReturnsExplanatoryUnavailableContext()
    {
        var executor = new GraphRagSearchExecutor(
            graphRagService: null,
            NullLogger<GraphRagSearchExecutor>.Instance);

        var result = await executor.HandleAsync(Query(), Mock.Of<IWorkflowContext>());

        result.AssembledText.Should().Contain("GraphRAG retrieval is unavailable")
            .And.Contain("AppConfig:AI:Rag:GraphDatabase:Enabled",
                "the degraded context must name the config knob that re-enables the feature");
        result.TotalTokens.Should().Be(0);
        result.WasTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ServicePresent_DelegatesToGlobalSearch()
    {
        var expected = new RagAssembledContext
        {
            AssembledText = "themes",
            TotalTokens = 2,
            WasTruncated = false
        };
        var service = new Mock<IGraphRagService>();
        service
            .Setup(s => s.GlobalSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var executor = new GraphRagSearchExecutor(
            service.Object,
            NullLogger<GraphRagSearchExecutor>.Instance);

        var result = await executor.HandleAsync(Query(), Mock.Of<IWorkflowContext>());

        result.Should().BeSameAs(expected);
    }
}

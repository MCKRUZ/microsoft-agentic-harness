using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.Tests.Helpers;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

public sealed class GraphRetrievalSourceTests
{
    private readonly Mock<IGraphRagService> _graphRag = new();

    [Fact]
    public async Task RetrieveAsync_DelegatesToLocalSearch_ReturnsWrappedResult()
    {
        var expected = RagTestData.CreateRetrievalResult(id: "graph-1", fusedScore: 0.8);
        _graphRag
            .Setup(g => g.LocalSearchAsync("test query", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([expected]);

        var sut = new GraphRetrievalSource(_graphRag.Object);

        var result = await sut.RetrieveAsync("test query", 10, QueryComplexity.Moderate, CancellationToken.None);

        result.SourceName.Should().Be("graph");
        result.Results.Should().HaveCount(1);
        result.Results[0].FusedScore.Should().Be(0.8);
        result.Latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void SourceName_IsGraph()
    {
        var sut = new GraphRetrievalSource(_graphRag.Object);
        sut.SourceName.Should().Be("graph");
    }
}

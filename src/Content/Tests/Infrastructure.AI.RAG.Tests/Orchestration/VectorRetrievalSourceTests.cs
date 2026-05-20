using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.Tests.Helpers;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

public sealed class VectorRetrievalSourceTests
{
    private readonly Mock<IHybridRetriever> _retriever = new();

    [Fact]
    public async Task RetrieveAsync_DelegatesToHybridRetriever_ReturnsWrappedResult()
    {
        var expected = RagTestData.CreateRetrievalResult(id: "chunk-1", fusedScore: 0.85);
        _retriever
            .Setup(r => r.RetrieveAsync("test query", 10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([expected]);

        var sut = new VectorRetrievalSource(_retriever.Object);

        var result = await sut.RetrieveAsync("test query", 10, QueryComplexity.Moderate, CancellationToken.None);

        result.SourceName.Should().Be("vector");
        result.Results.Should().HaveCount(1);
        result.Results[0].FusedScore.Should().Be(0.85);
        result.Latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void SourceName_IsVector()
    {
        var sut = new VectorRetrievalSource(_retriever.Object);
        sut.SourceName.Should().Be("vector");
    }
}

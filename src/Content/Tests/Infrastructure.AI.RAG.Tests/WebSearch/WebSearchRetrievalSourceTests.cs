using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.WebSearch;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.WebSearch;

public sealed class WebSearchRetrievalSourceTests
{
    private readonly Mock<IWebSearchProvider> _provider = new();

    [Fact]
    public async Task RetrieveAsync_ConvertsWebResultsToRetrievalResults()
    {
        _provider
            .Setup(p => p.SearchAsync("test", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new WebSearchResult { Title = "Page 1", Snippet = "Content 1", Url = "https://example.com/1" },
                new WebSearchResult { Title = "Page 2", Snippet = "Content 2", Url = "https://example.com/2" }
            ]);

        var sut = new WebSearchRetrievalSource(_provider.Object);

        var result = await sut.RetrieveAsync("test", 5, QueryComplexity.Complex, CancellationToken.None);

        result.SourceName.Should().Be("web_search");
        result.Results.Should().HaveCount(2);
        result.Results[0].FusedScore.Should().BeGreaterThan(result.Results[1].FusedScore,
            "first result should score higher (rank-decay)");
        result.Results[0].Chunk.Content.Should().Contain("Content 1");
        result.Latency.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyProviderResults_ReturnsEmptySourceResult()
    {
        _provider
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = new WebSearchRetrievalSource(_provider.Object);

        var result = await sut.RetrieveAsync("test", 5, QueryComplexity.Complex, CancellationToken.None);

        result.Results.Should().BeEmpty();
    }

    [Fact]
    public void SourceName_IsWebSearch()
    {
        var sut = new WebSearchRetrievalSource(_provider.Object);
        sut.SourceName.Should().Be("web_search");
    }
}

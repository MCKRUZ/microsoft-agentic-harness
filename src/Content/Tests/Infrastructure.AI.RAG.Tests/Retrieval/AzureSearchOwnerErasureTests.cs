using Application.AI.Common.Interfaces.RAG;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Right-to-erasure (#900) for the Azure AI Search stores, through the shared owner-erasure loop both of them call: every
/// chunk stamped with the person's owner id is deleted, across as many search pages as it takes, and the loop fails
/// rather than report a count it cannot stand behind.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. The index is reached only through a filter, so the filter string is the over-deletion
/// guard (asserted exactly, including quote escaping), and the paging and failure paths are the under-deletion guards.
/// </remarks>
public sealed class AzureSearchOwnerErasureTests
{
    private static readonly string[] NoMatches = [];

    private readonly Mock<SearchClient> _client = new();
    private readonly List<SearchOptions> _searches = [];
    private readonly List<string[]> _deleteBatches = [];

    [Fact]
    public async Task EraseSubjectAsync_MatchesSpanSeveralPages_DeletesEveryPageAndCountsEachChunkOnce()
    {
        var firstPage = Enumerable.Range(0, 1000).Select(i => $"chunk-{i}").ToArray();
        // The index has not caught up: the second page repeats an id already deleted alongside new ones.
        GivenSearchPages(firstPage, ["chunk-0", "chunk-1000", "chunk-1001"], []);
        GivenDeletesSucceed();

        var removed = await VectorStore().EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        removed.Should().Be(1002, "a repeated, already-deleted id must not be counted twice");
        _searches.Should().HaveCount(3, "the loop pages until a search matches nothing");
        _deleteBatches.Should().HaveCount(2);
        _deleteBatches[0].Should().Equal(firstPage);
        _deleteBatches[1].Should().Equal(["chunk-1000", "chunk-1001"], "an already-deleted id is not sent again");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteDocument_MoreThanOnePageOfChunks_DeletesEveryPage(bool vectorStore)
    {
        // A single search page was all a document delete used to remove (#901): a document of more than a thousand chunks
        // was left part-indexed while the removal reported success.
        var firstPage = Enumerable.Range(0, 1000).Select(i => $"doc-1_chunk_{i}").ToArray();
        GivenSearchPages(firstPage, ["doc-1_chunk_1000"], []);
        GivenDeletesSucceed();

        var removed = vectorStore
            ? await VectorStore().DeleteFromAllCollectionsAsync("doc-1", CancellationToken.None)
            : await Bm25Store().DeleteFromAllCollectionsAsync("doc-1", CancellationToken.None);

        removed.Should().Be(1001);
        _searches.Should().AllSatisfy(s => s.Filter.Should().Be("documentId eq 'doc-1'"));
        _deleteBatches.Should().HaveCount(2);
    }

    [Fact]
    public async Task EraseSubjectAsync_PagesOnlyRepeatDeletedChunks_RetriesThenThrowsInsteadOfReportingACount()
    {
        // Waits one second per stale round, five rounds, before giving up: the one deliberately slow test here.
        GivenSearchPages(["chunk-0"], ["chunk-0"]);
        GivenDeletesSucceed();

        var act = () => VectorStore().EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a page of stale results can hide chunks still to delete, so success cannot be claimed");
        _searches.Should().HaveCount(7, "one productive page, then the stale page retried until the bound is exceeded");
        _deleteBatches.Should().ContainSingle();
    }

    [Fact]
    public async Task EraseSubjectAsync_PerChunkDeleteFailure_Throws()
    {
        GivenSearchPages(["chunk-0", "chunk-1"], []);
        _client
            .Setup(c => c.DeleteDocumentsAsync(
                It.IsAny<IEnumerable<SearchDocument>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IndexResponse(true, false));

        var act = () => VectorStore().EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*1 of 2*");
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerContainingQuotes_IsEscapedSoTheFilterCannotWiden()
    {
        GivenSearchPages(NoMatches);

        var removed = await Bm25Store().EraseSubjectAsync(
            new ErasureSubject("x' or ownerId ne 'y", []), CancellationToken.None);

        removed.Should().Be(0);
        var options = _searches.Should().ContainSingle().Subject;
        options.Filter.Should().Be("ownerId eq 'x'' or ownerId ne ''y'", "each quote is doubled, so the id stays one literal");
        options.Select.Should().Equal(["id"]);
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerAsIssued_IsMatchedExactlyNotCanonicalized()
    {
        // Ingest stamps the owner as issued; a canonicalized filter would miss "Alice" and reach "alice".
        GivenSearchPages(NoMatches);

        await VectorStore().EraseSubjectAsync(new ErasureSubject("Alice", []), CancellationToken.None);

        _searches.Should().ContainSingle().Which.Filter.Should().Be("ownerId eq 'Alice'");
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerWithNothingIndexed_ReturnsZeroAndDeletesNothing()
    {
        GivenSearchPages(NoMatches);

        var removed = await VectorStore().EraseSubjectAsync(new ErasureSubject("nobody", []), CancellationToken.None);

        removed.Should().Be(0);
        _client.Verify(
            c => c.DeleteDocumentsAsync(
                It.IsAny<IEnumerable<SearchDocument>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private AzureAISearchVectorStore VectorStore() =>
        new(_client.Object, indexConfigured: true, Mock.Of<IEmbeddingService>(), NullLogger<AzureAISearchVectorStore>.Instance);

    private AzureAISearchBm25Store Bm25Store() =>
        new(_client.Object, indexConfigured: true, NullLogger<AzureAISearchBm25Store>.Instance);

    /// <summary>Each search returns the next page; once they run out, the last one repeats.</summary>
    private void GivenSearchPages(params string[][] pages)
    {
        _client
            .Setup(c => c.SearchAsync<SearchDocument>(
                It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string _, SearchOptions options, CancellationToken _) =>
            {
                _searches.Add(options);
                var ids = pages[Math.Min(_searches.Count - 1, pages.Length - 1)];
                var results = SearchModelFactory.SearchResults(
                    ids.Select(id => SearchModelFactory.SearchResult(new SearchDocument { ["id"] = id }, 1.0, null)),
                    totalCount: null,
                    facets: null,
                    coverage: null,
                    rawResponse: Mock.Of<Response>());
                return Task.FromResult(Response.FromValue(results, Mock.Of<Response>()));
            });
    }

    private void GivenDeletesSucceed() =>
        _client
            .Setup(c => c.DeleteDocumentsAsync(
                It.IsAny<IEnumerable<SearchDocument>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<SearchDocument> batch, IndexDocumentsOptions _, CancellationToken _) =>
            {
                var ids = batch.Select(d => (string)d["id"]).ToArray();
                _deleteBatches.Add(ids);
                return Task.FromResult(IndexResponse(ids.Select(_ => true).ToArray()));
            });

    private static Response<IndexDocumentsResult> IndexResponse(params bool[] outcomes) =>
        Response.FromValue(
            SearchModelFactory.IndexDocumentsResult(outcomes.Select((ok, i) => SearchModelFactory.IndexingResult(
                key: $"chunk-{i}", errorMessage: ok ? null : "failed", succeeded: ok, status: ok ? 200 : 500))),
            Mock.Of<Response>());
}

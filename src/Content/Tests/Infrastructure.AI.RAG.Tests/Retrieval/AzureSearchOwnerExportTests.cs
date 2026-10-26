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
/// Personal-data export (#902) for the Azure AI Search stores, through the shared reader both call: every chunk stamped with
/// the person's owner id, across as many pages as it takes, through the same filter erasure deletes by, and a failure
/// rather than a copy with chunks silently missing.
/// </summary>
/// <remarks>
/// A read cannot advance the way erasure does, by removing what it matched, so the paging is the under-reporting risk
/// specific to this store: a reader that stopped at the first page would hand a person who ingested more than a thousand
/// chunks a copy that looks complete. The fake index here serves pages by <c>$skip</c> and reports a total, as the
/// service does.
/// </remarks>
public sealed class AzureSearchOwnerExportTests
{
    private readonly Mock<SearchClient> _client = new();
    private readonly List<SearchOptions> _searches = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportSubjectAsync_MatchesSpanSeveralPages_ReadsEveryChunkOnceByPagingOnId(bool vectorStore)
    {
        var index = Enumerable.Range(0, 2500).Select(i => $"chunk-{i:D4}").ToArray();
        GivenIndex(index);

        var records = vectorStore
            ? await VectorStore().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None)
            : await Bm25Store().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        records.Select(r => r.GetProperty("id").GetString()).Should().Equal(index, "every chunk, each once, in id order");
        _searches.Select(s => s.Filter).Should().Equal(
            "ownerId eq 'user-a'",
            "ownerId eq 'user-a' and id gt 'chunk-0999'",
            "ownerId eq 'user-a' and id gt 'chunk-1999'");
        _searches.Should().AllSatisfy(s =>
        {
            s.OrderBy.Should().Equal(["id asc"], "ties on score would otherwise reorder between pages");
            s.Skip.Should().BeNull("$skip pages are unstable and stop at 100,000");
            s.Select.Should().BeEquivalentTo(["id", "documentId", "content", "sectionPath"],
                "never the embedding, nor the owner and tenant stamps");
        });
        _searches[0].IncludeTotalCount.Should().BeTrue();
    }

    [Fact]
    public async Task ExportSubjectAsync_ReadsFewerChunksThanTheTotal_ThrowsInsteadOfReturningAShortCopy()
    {
        // The index changed during the read: the total counted a chunk the pages never returned.
        GivenIndex(Enumerable.Range(0, 1000).Select(i => $"chunk-{i:D4}").ToArray(), total: 1001);

        var act = () => VectorStore().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*1000 distinct*1001*");
    }

    [Fact]
    public async Task ExportSubjectAsync_ServiceIgnoresTheIdBound_ThrowsInsteadOfLoopingForever()
    {
        var page = Enumerable.Range(0, 1000).Select(i => $"chunk-{i:D4}").ToArray();
        Serve(_ => page, total: 2000);

        var act = () => VectorStore().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*could not advance*");
        _searches.Should().HaveCount(2, "the second identical page is the evidence; there is no third request");
    }

    [Fact]
    public async Task ExportSubjectAsync_ChunkIdContainingQuotes_IsEscapedInTheNextPagesFilter()
    {
        var index = Enumerable.Range(0, 999).Select(i => $"a-{i:D4}").Append("b'x").ToArray();
        GivenIndex(index);

        await Bm25Store().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        _searches[1].Filter.Should().Be("ownerId eq 'user-a' and id gt 'b''x'");
    }

    [Fact]
    public async Task ExportSubjectAsync_OwnerContainingQuotes_IsEscapedSoTheFilterCannotWiden()
    {
        GivenIndex([], total: 0);

        var records = await VectorStore().ExportSubjectAsync(
            new ExportSubject("x' or ownerId ne 'y", []), CancellationToken.None);

        records.Should().BeEmpty();
        _searches.Should().ContainSingle().Which.Filter.Should().Be("ownerId eq 'x'' or ownerId ne ''y'");
    }

    [Fact]
    public void BothStores_ExportUnderOneName_SoTheSharedIndexIsReadOnce()
    {
        VectorStore().ExportStoreName.Should().Be(Bm25Store().ExportStoreName);
    }

    private AzureAISearchVectorStore VectorStore() =>
        new(_client.Object, Mock.Of<IEmbeddingService>(), NullLogger<AzureAISearchVectorStore>.Instance);

    private AzureAISearchBm25Store Bm25Store() =>
        new(_client.Object, NullLogger<AzureAISearchBm25Store>.Instance);

    /// <summary>
    /// An index holding <paramref name="ids"/> for the owner. Each search returns, in id order, up to a page of those after
    /// the <c>id gt</c> bound in its filter, the way the service answers a filtered, ordered query.
    /// </summary>
    private void GivenIndex(string[] ids, long? total = null) =>
        Serve(options =>
        {
            var bound = AfterBound(options.Filter);
            return [.. ids.Order(StringComparer.Ordinal)
                .Where(id => bound is null || string.CompareOrdinal(id, bound) > 0)
                .Take(options.Size ?? 50)];
        }, total ?? ids.Length);

    private static string? AfterBound(string filter)
    {
        const string marker = " and id gt '";
        var at = filter.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : filter[(at + marker.Length)..^1].Replace("''", "'", StringComparison.Ordinal);
    }

    private void Serve(Func<SearchOptions, string[]> page, long total)
    {
        _client
            .Setup(c => c.SearchAsync<SearchDocument>(
                It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string _, SearchOptions options, CancellationToken _) =>
            {
                _searches.Add(options);
                var results = SearchModelFactory.SearchResults(
                    page(options).Select(id => SearchModelFactory.SearchResult(
                        new SearchDocument
                        {
                            ["id"] = id,
                            ["documentId"] = $"doc-{id}",
                            ["content"] = $"text of {id}",
                            ["sectionPath"] = "Notes",
                        }, 1.0, null)),
                    totalCount: total,
                    facets: null,
                    coverage: null,
                    rawResponse: Mock.Of<Response>());
                return Task.FromResult(Response.FromValue(results, Mock.Of<Response>()));
            });
    }
}

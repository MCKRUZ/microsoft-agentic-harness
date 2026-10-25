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
    public async Task ExportSubjectAsync_MatchesSpanSeveralPages_ReadsEveryPageOnce(bool vectorStore)
    {
        var index = Enumerable.Range(0, 2500).Select(i => $"chunk-{i:D4}").ToArray();
        GivenIndex(index, total: index.Length);

        var records = vectorStore
            ? await VectorStore().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None)
            : await Bm25Store().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        records.Select(r => r.GetProperty("id").GetString()).Should().Equal(index, "every page, each chunk once, by id");
        _searches.Select(s => s.Skip).Should().Equal(0, 1000, 2000);
        _searches.Should().AllSatisfy(s =>
        {
            s.Filter.Should().Be("ownerId eq 'user-a'", "the filter erasure deletes by, owner as issued");
            s.Select.Should().BeEquivalentTo(["id", "documentId", "content", "sectionPath"],
                "never the embedding, nor the owner and tenant stamps");
            s.IncludeTotalCount.Should().BeTrue();
        });
    }

    [Fact]
    public async Task ExportSubjectAsync_PagesRepeatAChunkAndMissAnother_ThrowsInsteadOfReturningAShortCopy()
    {
        // The service orders ties differently between two pages: one chunk appears twice, another never.
        var pageOne = Enumerable.Range(0, 1000).Select(i => $"chunk-{i:D4}").ToArray();
        GivenPages(total: 1001, pageOne, ["chunk-0999"]);

        var act = () => VectorStore().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*1000 distinct*1001*");
    }

    [Fact]
    public async Task ExportSubjectAsync_MoreMatchesThanSkipCanReach_Throws()
    {
        GivenPages(total: 200_000, ["chunk-0"]);

        var act = () => Bm25Store().ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
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

    /// <summary>An index holding <paramref name="ids"/> for the owner, served a page at a time by <c>$skip</c>.</summary>
    private void GivenIndex(string[] ids, long total) =>
        Serve(options => ids.Skip(options.Skip ?? 0).Take(options.Size ?? 50).ToArray(), total);

    /// <summary>Each search returns the next of <paramref name="pages"/>, then nothing.</summary>
    private void GivenPages(long total, params string[][] pages) =>
        Serve(_ => _searches.Count <= pages.Length ? pages[_searches.Count - 1] : [], total);

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

using Application.AI.Common.Interfaces.Compliance;
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
/// Neither Azure AI Search store reports a person erased, exported or a document removed without asking its index
/// (#1012). The host refuses to start with Azure AI Search chosen and no endpoint (<c>RagConfigValidator</c>); these
/// tests are the backstop for a composition that skips that check. An index that cannot be reached must surface as a
/// failure, so the erasure receipt reads <c>Partial</c>; reporting nothing instead is how a lost endpoint certified every
/// erasure complete while the person's chunks stayed in the real index.
/// </summary>
public sealed class AzureAISearchStoresAlwaysAskTheIndexTests
{
    private readonly Mock<SearchClient> _client = new(MockBehavior.Strict);

    public AzureAISearchStoresAlwaysAskTheIndexTests() =>
        _client
            .Setup(c => c.SearchAsync<SearchDocument>(
                It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException("No such host is known. (not-configured.search.windows.net:443)"));

    public static TheoryData<string> Stores => new() { "vector", "bm25" };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EraseSubject_WhenTheIndexCannotBeReached_Fails(string store)
    {
        var act = () => Eraser(store).EraseSubjectAsync(new ErasureSubject("person-1", []), CancellationToken.None);

        await act.Should().ThrowAsync<RequestFailedException>();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ExportSubject_WhenTheIndexCannotBeReached_Fails(string store)
    {
        var act = () => Exporter(store).ExportSubjectAsync(new ExportSubject("person-1", []), CancellationToken.None);

        await act.Should().ThrowAsync<RequestFailedException>();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task DeleteFromAllCollections_WhenTheIndexCannotBeReached_Fails(string store)
    {
        var act = () => store == "vector"
            ? VectorStore().DeleteFromAllCollectionsAsync("0123456789ABCDEF")
            : Bm25Store().DeleteFromAllCollectionsAsync("0123456789ABCDEF");

        await act.Should().ThrowAsync<RequestFailedException>();
    }

    private IPersonalDataEraser Eraser(string store) => store == "vector" ? VectorStore() : Bm25Store();

    private IPersonalDataExporter Exporter(string store) => store == "vector" ? VectorStore() : Bm25Store();

    private AzureAISearchVectorStore VectorStore() =>
        new(_client.Object, Mock.Of<IEmbeddingService>(), NullLogger<AzureAISearchVectorStore>.Instance);

    private AzureAISearchBm25Store Bm25Store() => new(_client.Object, NullLogger<AzureAISearchBm25Store>.Instance);
}

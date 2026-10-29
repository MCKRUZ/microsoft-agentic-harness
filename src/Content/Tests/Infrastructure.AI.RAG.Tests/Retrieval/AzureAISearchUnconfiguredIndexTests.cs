using Application.AI.Common.Interfaces.RAG;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Domain.AI.RAG.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// An Azure AI Search index with no endpoint configured holds nobody's data (#1003). The shipped default document index
/// is Azure AI Search, so a host left on it, the Execution API among them, pointed erasure at the placeholder endpoint:
/// both stores failed, every receipt read <c>Partial</c>, and the person's conversations and plans were held back on
/// every retry. Erasure, export and deletes now report nothing without calling the client; writes still reach it.
/// </summary>
public sealed class AzureAISearchUnconfiguredIndexTests
{
    private readonly Mock<SearchClient> _client = new(MockBehavior.Strict);

    public static TheoryData<string> Stores => new() { "vector", "bm25" };

    private AzureStore Create(string store, bool configured) => store == "vector"
        ? new(new AzureAISearchVectorStore(_client.Object, configured, Mock.Of<IEmbeddingService>(),
            NullLogger<AzureAISearchVectorStore>.Instance))
        : new(new AzureAISearchBm25Store(_client.Object, configured, NullLogger<AzureAISearchBm25Store>.Instance));

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EraseSubject_Unconfigured_RemovesNothingAndNeverCallsTheIndex(string store)
    {
        var removed = await Create(store, configured: false).Eraser.EraseSubjectAsync(
            new ErasureSubject("person-1", []), CancellationToken.None);

        removed.Should().Be(0);
        _client.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ExportSubject_Unconfigured_ReturnsNothingAndNeverCallsTheIndex(string store)
    {
        var records = await Create(store, configured: false).Exporter.ExportSubjectAsync(
            new ExportSubject("person-1", []), CancellationToken.None);

        records.Should().BeEmpty();
        _client.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task DeleteFromAllCollections_Unconfigured_RemovesNothingAndNeverCallsTheIndex(string store)
    {
        var removed = await Create(store, configured: false).DeleteFromAllCollectionsAsync("0123456789ABCDEF");

        removed.Should().Be(0);
        _client.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Index_Unconfigured_StillReachesTheIndex(string store)
    {
        // Only the reads and deletes of what cannot be there are skipped: a write must still fail on the placeholder,
        // or ingestion would report documents stored that never were.
        var client = new Mock<SearchClient>();
        client.Setup(c => c.MergeOrUploadDocumentsAsync(
                It.IsAny<IEnumerable<SearchDocument>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                SearchModelFactory.IndexDocumentsResult([SearchModelFactory.IndexingResult("chunk-0", null, true, 200)]),
                Mock.Of<Response>()));
        IReadOnlyList<DocumentChunk> chunks = [RagTestData.CreateChunk(id: "chunk-0", documentId: "doc-1")];

        if (store == "vector")
        {
            await new AzureAISearchVectorStore(client.Object, false, Mock.Of<IEmbeddingService>(),
                NullLogger<AzureAISearchVectorStore>.Instance).IndexAsync(chunks);
        }
        else
        {
            await new AzureAISearchBm25Store(client.Object, false, NullLogger<AzureAISearchBm25Store>.Instance)
                .IndexAsync(chunks);
        }

        client.Verify(c => c.MergeOrUploadDocumentsAsync(
            It.IsAny<IEnumerable<SearchDocument>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Either Azure store, seen through the three surfaces under test.</summary>
    private sealed class AzureStore
    {
        private readonly Func<string, Task<int>> _deleteAll;

        public AzureStore(AzureAISearchVectorStore store)
        {
            Eraser = store;
            Exporter = store;
            _deleteAll = id => store.DeleteFromAllCollectionsAsync(id);
        }

        public AzureStore(AzureAISearchBm25Store store)
        {
            Eraser = store;
            Exporter = store;
            _deleteAll = id => store.DeleteFromAllCollectionsAsync(id);
        }

        public Application.AI.Common.Interfaces.Compliance.IPersonalDataEraser Eraser { get; }

        public Application.AI.Common.Interfaces.Compliance.IPersonalDataExporter Exporter { get; }

        public Task<int> DeleteFromAllCollectionsAsync(string documentId) => _deleteAll(documentId);
    }
}

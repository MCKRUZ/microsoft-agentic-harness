using Application.AI.Common.Interfaces.RAG;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Verifies the Azure stores' partial-batch handling: <c>MergeOrUploadDocumentsAsync</c>
/// reports per-document failures WITHOUT throwing, so a store that ignored
/// <c>IndexingResult.Succeeded</c> would let the ingest command report Success while
/// chunks (and their provenance stamps) silently never landed. Both stores must fail the
/// operation on any per-document failure so the ingest handler's compensation runs.
/// </summary>
public sealed class AzureAISearchStoreIndexingFailureTests
{
    private static Response<IndexDocumentsResult> BatchResponse(params bool[] outcomes)
    {
        var results = outcomes
            .Select((succeeded, i) => SearchModelFactory.IndexingResult(
                key: $"chunk-{i}",
                errorMessage: succeeded ? null : "storage quota exceeded",
                succeeded: succeeded,
                status: succeeded ? 200 : 503))
            .ToList();
        return Response.FromValue(
            SearchModelFactory.IndexDocumentsResult(results), Mock.Of<Response>());
    }

    private static Mock<SearchClient> ClientReturning(Response<IndexDocumentsResult> response)
    {
        var client = new Mock<SearchClient>();
        client
            .Setup(c => c.MergeOrUploadDocumentsAsync(
                It.IsAny<IEnumerable<SearchDocument>>(),
                It.IsAny<IndexDocumentsOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return client;
    }

    private static IReadOnlyList<DocumentChunk> Chunks(int count) =>
        Enumerable.Range(0, count)
            .Select(i => RagTestData.CreateChunk(id: $"chunk-{i}", documentId: "doc-1"))
            .ToList();

    [Fact]
    public async Task VectorStore_IndexAsync_PartialBatchFailure_Throws()
    {
        var store = new AzureAISearchVectorStore(
            ClientReturning(BatchResponse(true, false)).Object,
            Mock.Of<IEmbeddingService>(),
            NullLogger<AzureAISearchVectorStore>.Instance);

        var act = () => store.IndexAsync(Chunks(2));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*1 of 2*", "the failure must name the failed count");
    }

    [Fact]
    public async Task VectorStore_IndexAsync_AllSucceeded_DoesNotThrow()
    {
        var store = new AzureAISearchVectorStore(
            ClientReturning(BatchResponse(true, true)).Object,
            Mock.Of<IEmbeddingService>(),
            NullLogger<AzureAISearchVectorStore>.Instance);

        await store.Invoking(s => s.IndexAsync(Chunks(2))).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Bm25Store_IndexAsync_PartialBatchFailure_Throws()
    {
        var store = new AzureAISearchBm25Store(
            ClientReturning(BatchResponse(false, true, true)).Object,
            NullLogger<AzureAISearchBm25Store>.Instance);

        var act = () => store.IndexAsync(Chunks(3));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*1 of 3*");
    }

    [Fact]
    public async Task Bm25Store_IndexAsync_AllSucceeded_DoesNotThrow()
    {
        var store = new AzureAISearchBm25Store(
            ClientReturning(BatchResponse(true)).Object,
            NullLogger<AzureAISearchBm25Store>.Instance);

        await store.Invoking(s => s.IndexAsync(Chunks(1))).Should().NotThrowAsync();
    }
}

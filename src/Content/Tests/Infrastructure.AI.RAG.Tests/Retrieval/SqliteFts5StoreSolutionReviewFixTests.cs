using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Regression tests for the contentless FTS5 table defect (solution review finding 7).
/// <para>
/// <see cref="SqliteFts5Store"/> previously created its virtual table with <c>content=''</c>, making
/// it a contentless FTS5 table that indexes tokens but stores no column values. As a result
/// <c>SearchAsync</c> read NULLs (throwing on <c>GetString</c>) and <c>DeleteAsync</c>'s
/// <c>WHERE document_id = ...</c> matched zero rows — local-dev BM25 was permanently dead. The fix
/// drops <c>content=''</c> so the table stores its columns; these tests assert search returns
/// populated rows and delete actually removes a document's chunks.
/// </para>
/// </summary>
public sealed class SqliteFts5StoreSolutionReviewFixTests
{
    private static SqliteFts5Store CreateStore() =>
        new(
            // Unique shared in-memory DB per test instance so tests do not collide.
            $"Data Source=Fts5Fix-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
            NullLogger<SqliteFts5Store>.Instance);

    private static DocumentChunk Chunk(string id, string content, string documentId) =>
        RagTestData.CreateChunk(id: id, content: content, documentId: documentId);

    [Fact]
    public async Task SearchAsync_AfterIndexing_ReturnsPopulatedChunkColumns()
    {
        // Arrange
        var store = CreateStore();
        var chunks = new List<DocumentChunk>
        {
            Chunk("c1", "the quick brown fox jumps", documentId: "doc-1"),
            Chunk("c2", "lazy dogs sleep all afternoon", documentId: "doc-2"),
        };
        await store.IndexAsync(chunks);

        // Act
        var results = await store.SearchAsync("fox", topK: 10);

        // Assert — a contentless table would have thrown on GetString or returned nothing;
        // the fixed table returns the row with all columns populated.
        results.Should().ContainSingle();
        var hit = results[0].Chunk;
        hit.Id.Should().Be("c1");
        hit.DocumentId.Should().Be("doc-1");
        hit.Content.Should().Contain("fox");
        hit.SectionPath.Should().NotBeNullOrEmpty();
        results[0].SparseScore.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAllChunksForDocument()
    {
        // Arrange
        var store = CreateStore();
        await store.IndexAsync(new List<DocumentChunk>
        {
            Chunk("c1", "alpha beta gamma keyword", documentId: "doc-del"),
            Chunk("c2", "alpha beta gamma keyword", documentId: "doc-keep"),
        });

        // Act — delete by document id (a no-op against a contentless table where document_id is NULL).
        await store.DeleteAsync("doc-del");

        var results = await store.SearchAsync("keyword", topK: 10);

        // Assert — only the surviving document's chunk remains.
        results.Should().ContainSingle();
        results[0].Chunk.DocumentId.Should().Be("doc-keep");
    }
}

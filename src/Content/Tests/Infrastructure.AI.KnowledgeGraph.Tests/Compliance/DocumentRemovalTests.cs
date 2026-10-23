using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Common.Interfaces.Common;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.Feedback;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

/// <summary>
/// Removing a source document (#901) takes out what was drawn from it and nothing more, against real graph stores: a node
/// only it produced goes, a node another document also produced keeps that document's references, an edge drawn from its
/// text goes, and in the GraphRAG corpus graph every community summary built over the changed knowledge goes too.
/// </summary>
public sealed class DocumentRemovalTests : IDisposable
{
    private const string Removed = "doc-a";
    private const string Kept = "doc-b";

    private readonly string _corpusDir = Path.Combine(Path.GetTempPath(), $"document-removal-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _graph = new(NullLogger<InMemoryGraphStore>.Instance);
    private readonly GraphFeedbackStore _feedback = new(NullLogger<GraphFeedbackStore>.Instance, TimeProvider.System);
    private readonly Mock<IVectorStore> _vectors = new();
    private readonly Mock<IBm25Store> _bm25 = new();
    private readonly Mock<IMemoryAuditSink> _audit = new();
    private readonly KuzuGraphBackend _corpus;
    private readonly DefaultErasureOrchestrator _sut;

    public DocumentRemovalTests()
    {
        _corpus = new KuzuGraphBackend(_corpusDir, NullLogger<KuzuGraphBackend>.Instance, new PlainCreator());
        _vectors.Setup(v => v.DeleteFromAllCollectionsAsync(Removed, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        _bm25.Setup(b => b.DeleteFromAllCollectionsAsync(Removed, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        _sut = new DefaultErasureOrchestrator(
            _graph, _feedback, _vectors.Object, _audit.Object, TimeProvider.System,
            NullLogger<DefaultErasureOrchestrator>.Instance, _bm25.Object, crossSessionMemory: null, corpusGraph: _corpus);
    }

    public void Dispose()
    {
        _corpus.Dispose();
        try
        {
            Directory.Delete(_corpusDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task ANodeOnlyTheDocumentProduced_IsDeleted_WithItsEdgesAndFeedback()
    {
        await _graph.AddNodesAsync([Node("only-a", Chunk(Removed, 0)), Node("other", Chunk(Kept, 0))]);
        await _graph.AddEdgesAsync([Edge("e1", "only-a", "other", Chunk(Kept, 1))]);
        await _feedback.ApplyNodeFeedbackAsync("only-a", feedbackScore: 5.0, alpha: 0.3);

        var receipt = await _sut.RemoveDocumentAsync(Removed);

        (await _graph.GetNodeAsync("only-a")).Should().BeNull();
        (await _graph.GetNodeAsync("other")).Should().NotBeNull();
        (await _graph.GetAllEdgesAsync()).Should().BeEmpty("an edge touching a deleted node goes with it");
        receipt.NodesDeleted.Should().Be(1);
        receipt.FeedbackWeightsDeleted.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ANodeAnotherDocumentAlsoProduced_KeepsThatDocumentsReferences_AndSurvives()
    {
        await _graph.AddNodesAsync([Node("shared", Chunk(Removed, 0), Chunk(Kept, 0), Chunk(Removed, 1))]);

        await _sut.RemoveDocumentAsync(Removed);

        var shared = await _graph.GetNodeAsync("shared");
        shared.Should().NotBeNull();
        shared!.ChunkIds.Should().Equal(Chunk(Kept, 0));
    }

    [Fact]
    public async Task AnEdgeDrawnFromTheDocument_Goes_EvenBetweenSurvivingNodes_AndOthersStay()
    {
        await _graph.AddNodesAsync([Node("x", Chunk(Kept, 0)), Node("y", Chunk(Kept, 1))]);
        await _graph.AddEdgesAsync([Edge("from-a", "x", "y", Chunk(Removed, 4)), Edge("from-b", "y", "x", Chunk(Kept, 2))]);

        var receipt = await _sut.RemoveDocumentAsync(Removed);

        (await _graph.GetAllEdgesAsync()).Select(e => e.Id).Should().Equal("from-b");
        receipt.EdgesDeleted.Should().Be(1);
    }

    [Theory]
    [InlineData("doc-a_v2")]
    [InlineData("doc")]
    [InlineData("xdoc-a")]
    public async Task ADocumentWhoseIdMerelyResemblesTheRemovedOne_IsUntouched(string otherDocument)
    {
        // Which document a chunk belongs to is read from its id by one rule; a prefix or substring match would reach
        // another document's knowledge.
        await _graph.AddNodesAsync([Node("lookalike", Chunk(otherDocument, 0))]);
        await _graph.AddEdgesAsync([Edge("lookalike-edge", "lookalike", "lookalike", Chunk(otherDocument, 1))]);

        await _sut.RemoveDocumentAsync(Removed);

        (await _graph.GetNodeAsync("lookalike")).Should().NotBeNull();
        (await _graph.GetAllEdgesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task TheCorpusGraph_LosesTheDocumentsKnowledge_AndEveryCommunitySummaryBuiltOverIt()
    {
        await _corpus.AddNodesAsync([
            Node("c-only", Chunk(Removed, 0)), Node("c-shared", Chunk(Removed, 1), Chunk(Kept, 0)), Node("c-other", Chunk(Kept, 1)),
        ]);
        await _corpus.SaveCommunityAsync(Community("about-a", "c-only", "c-other"));
        await _corpus.SaveCommunityAsync(Community("shared-only", "c-shared"));
        await _corpus.SaveCommunityAsync(Community("untouched", "c-other"));

        await _sut.RemoveDocumentAsync(Removed);

        (await _corpus.GetNodeAsync("c-only")).Should().BeNull();
        (await _corpus.GetNodeAsync("c-shared"))!.ChunkIds.Should().Equal(Chunk(Kept, 0));
        (await _corpus.GetCommunitiesAsync(0)).Select(c => c.Id).Should().Equal(["untouched"],
            "a summary written over text from the removed document would still repeat it");
    }

    [Fact]
    public async Task TheDocumentsChunks_AreDeletedFromEveryCollection_AndTheRemovalIsAuditedAsSuch()
    {
        var receipt = await _sut.RemoveDocumentAsync(Removed);

        receipt.VectorEmbeddingsDeleted.Should().Be(3);
        receipt.Bm25DocumentsDeleted.Should().Be(3);
        receipt.UnmatchedDocumentIds.Should().BeEmpty();
        _audit.Verify(a => a.EmitAsync(
            It.Is<MemoryAuditEvent>(e => e.Action == MemoryAuditAction.DocumentRemoval), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ADocumentNothingHolds_IsReportedAsUnmatched()
    {
        var receipt = await _sut.RemoveDocumentAsync("never-loaded");

        receipt.UnmatchedDocumentIds.Should().Equal("never-loaded");
    }

    private static string Chunk(string documentId, int index) => $"{documentId}_chunk_{index}";

    private static GraphNode Node(string id, params string[] chunkIds) => new()
    {
        Id = id, Name = id, Type = "Entity", ChunkIds = chunkIds,
    };

    private static GraphEdge Edge(string id, string source, string target, string chunkId) => new()
    {
        Id = id, SourceNodeId = source, TargetNodeId = target, Predicate = "relates_to", ChunkId = chunkId,
    };

    private static Community Community(string id, params string[] nodeIds) => new()
    {
        Id = id, Level = 0, Summary = $"summary of {id}", NodeIds = nodeIds, Modularity = 0.5,
    };

    private sealed class PlainCreator : IOwnerOnlyDirectoryCreator
    {
        public void Create(string directory, Microsoft.Extensions.Logging.ILogger? logger = null) =>
            Directory.CreateDirectory(directory);
    }
}

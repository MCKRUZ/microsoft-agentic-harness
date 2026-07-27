using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.CQRS.RAG.IngestDocument;
using Application.Core.Workflows.KnowledgeGraph;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.RAG;

/// <summary>
/// Verifies the two opt-in graph-build stages of <see cref="IngestDocumentCommandHandler"/>:
/// corpus-graph indexing (<c>GraphRag.IndexOnIngest</c>) and knowledge-graph enrichment
/// (<c>GraphRag.EnrichKnowledgeGraphOnIngest</c>). Both default off, both feed the ingested
/// chunks forward when on, and neither may ever fail the core ingestion — the vector and
/// BM25 writes have already committed, so a graph failure must surface as an honest partial
/// success (<c>Success == true</c> with the stage flag <c>false</c>), not as a rollback.
/// </summary>
public sealed class IngestDocumentCommandHandlerGraphStagesTests
{
    private const string DocumentId = "file:///docs/report.md";
    private const string CollectionName = "corpus-a";

    private readonly Mock<IDocumentParser> _parser = new();
    private readonly Mock<IStructureExtractor> _structureExtractor = new();
    private readonly Mock<IChunkingService> _chunker = new();
    private readonly Mock<IContextualEnricher> _enricher = new();
    private readonly Mock<IRaptorSummarizer> _raptor = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IBm25Store> _bm25Store = new();
    private readonly Mock<IGraphRagService> _graphRag = new();

    private readonly IReadOnlyList<DocumentChunk> _chunks;

    public IngestDocumentCommandHandlerGraphStagesTests()
    {
        _chunks = new List<DocumentChunk>
        {
            new()
            {
                Id = $"{DocumentId}_chunk_0",
                DocumentId = DocumentId,
                SectionPath = "Root",
                Content = "chunk text",
                Tokens = 3,
                Metadata = new ChunkMetadata
                {
                    SourceUri = new Uri(DocumentId),
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                Embedding = new[] { 0.1f, 0.2f },
            },
        };

        _parser
            .Setup(p => p.ParseAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# markdown");
        _structureExtractor
            .Setup(s => s.ExtractStructure(It.IsAny<string>()))
            .Returns(new SkeletonNode { Title = "Root", Level = 1, StartOffset = 0, EndOffset = 10 });
        _chunker
            .Setup(c => c.ChunkAsync(
                It.IsAny<string>(), It.IsAny<SkeletonNode>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chunks);
        _embedding
            .Setup(e => e.EmbedAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chunks);
        _vectorStore
            .Setup(v => v.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _bm25Store
            .Setup(b => b.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private IngestDocumentCommandHandler CreateHandler(
        Action<AppConfig>? configure = null,
        Action<IServiceCollection>? registerServices = null)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.Ingestion.EnableContextualEnrichment = false;
        appConfig.AI.Rag.Ingestion.EnableRaptorSummaries = false;
        configure?.Invoke(appConfig);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(appConfig);

        var services = new ServiceCollection();
        registerServices?.Invoke(services);

        return new IngestDocumentCommandHandler(
            _parser.Object,
            _structureExtractor.Object,
            _chunker.Object,
            _enricher.Object,
            _raptor.Object,
            _embedding.Object,
            _vectorStore.Object,
            _bm25Store.Object,
            new Mock<IKnowledgeScope>().Object,
            NullLogger<IngestDocumentCommandHandler>.Instance,
            monitor.Object,
            services.BuildServiceProvider());
    }

    private static IngestDocumentCommand Command() => new()
    {
        DocumentUri = new Uri(DocumentId),
        CollectionName = CollectionName,
    };

    /// <summary>
    /// Builds a single-executor stand-in for the real "kg-ingestion" workflow so the
    /// handler's resolution, invocation, and failure isolation can be verified without the
    /// LLM-backed extraction pipeline (which has its own integration test against the
    /// decorated store in Infrastructure.AI.Tests).
    /// </summary>
    private static Workflow BuildStubWorkflow(Func<KgIngestionInput, KgIngestionResult> handle)
    {
        var stub = new StubKgIngestionExecutor(handle);
        var builder = new WorkflowBuilder(stub);
        builder.WithOutputFrom(stub);
        return builder.Build();
    }

    [Fact]
    public async Task Handle_GraphStagesDisabledByDefault_NeverTouchesGraphAndFlagsAreNull()
    {
        var workflowInvoked = false;
        var handler = CreateHandler(registerServices: services =>
        {
            services.AddSingleton(_graphRag.Object);
            services.AddKeyedSingleton("kg-ingestion", BuildStubWorkflow(input =>
            {
                workflowInvoked = true;
                return new KgIngestionResult(0, 0, input.Chunks.Count);
            }));
        });

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.GraphIndexed.Should().BeNull("the corpus-graph stage is off by default");
        result.KnowledgeGraphEnriched.Should().BeNull("the enrichment stage is off by default");
        workflowInvoked.Should().BeFalse();
        _graphRag.Verify(
            g => g.IndexCorpusAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_IndexOnIngestEnabled_IndexesEmbeddedChunksAndReportsTrue()
    {
        IReadOnlyList<DocumentChunk>? indexedChunks = null;
        _graphRag
            .Setup(g => g.IndexCorpusAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<DocumentChunk>, CancellationToken>((c, _) => indexedChunks = c)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(
            configure: c => c.AI.Rag.GraphRag.IndexOnIngest = true,
            registerServices: services => services.AddSingleton(_graphRag.Object));

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.GraphIndexed.Should().BeTrue();
        indexedChunks.Should().NotBeNull();
        indexedChunks!.Should().ContainSingle(c => c.Id == $"{DocumentId}_chunk_0",
            "the graph must be built from the same embedded chunks the vector/BM25 stores received");
    }

    [Fact]
    public async Task Handle_IndexOnIngestEnabled_GraphIndexingFails_IngestionStillSucceedsHonestly()
    {
        _graphRag
            .Setup(g => g.IndexCorpusAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("graph backend down"));

        var handler = CreateHandler(
            configure: c => c.AI.Rag.GraphRag.IndexOnIngest = true,
            registerServices: services => services.AddSingleton(_graphRag.Object));

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue("the vector and BM25 writes already committed");
        result.GraphIndexed.Should().BeFalse("the failure must be surfaced, not hidden");
        _vectorStore.Verify(
            v => v.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a graph failure must not trigger compensation of the committed stores");
        _bm25Store.Verify(
            b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_IndexOnIngestEnabled_ServiceAbsent_ReportsFalseWithoutFailing()
    {
        // IGraphRagService is registered only when the graph database backend is enabled.
        // RagConfigValidator rejects this combination at host startup, but the handler must
        // still be correct for containers composed without options validation.
        var handler = CreateHandler(configure: c => c.AI.Rag.GraphRag.IndexOnIngest = true);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.GraphIndexed.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_EnrichEnabled_RunsKgIngestionWorkflowOverChunksAndReportsTrue()
    {
        KgIngestionInput? received = null;
        var handler = CreateHandler(
            configure: c => c.AI.Rag.GraphRag.EnrichKnowledgeGraphOnIngest = true,
            registerServices: services => services.AddKeyedSingleton(
                "kg-ingestion",
                BuildStubWorkflow(input =>
                {
                    received = input;
                    return new KgIngestionResult(2, 1, input.Chunks.Count);
                })));

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.KnowledgeGraphEnriched.Should().BeTrue();
        received.Should().NotBeNull();
        received!.Chunks.Should().ContainSingle(c => c.Id == $"{DocumentId}_chunk_0");
        received.SourcePipeline.Should().Be("rag_ingestion",
            "provenance stamps must attribute the entities to the ingestion pipeline");
    }

    [Fact]
    public async Task Handle_EnrichEnabled_WorkflowFails_IngestionStillSucceedsHonestly()
    {
        var handler = CreateHandler(
            configure: c => c.AI.Rag.GraphRag.EnrichKnowledgeGraphOnIngest = true,
            registerServices: services => services.AddKeyedSingleton(
                "kg-ingestion",
                BuildStubWorkflow(_ => throw new InvalidOperationException("extraction exploded"))));

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue("the vector and BM25 writes already committed");
        result.KnowledgeGraphEnriched.Should().BeFalse();
        _vectorStore.Verify(
            v => v.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class StubKgIngestionExecutor(
        Func<KgIngestionInput, KgIngestionResult> handle)
        : Executor<KgIngestionInput, KgIngestionResult>("stub_kg_ingestion")
    {
        public override ValueTask<KgIngestionResult> HandleAsync(
            KgIngestionInput message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(handle(message));
    }
}

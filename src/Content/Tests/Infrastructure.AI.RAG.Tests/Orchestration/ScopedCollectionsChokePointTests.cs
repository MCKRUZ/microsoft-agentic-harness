using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Application.Core.Workflows.Rag;
using Domain.AI.Planner;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Planner.StepExecutors;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.QueryTransform;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Infrastructure.AI.Tools;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

/// <summary>
/// Regression tests for the ScopedCollections enforcement choke points. The MediatR
/// request validators only guard the HTTP/CQRS surface; these tests prove the paths that
/// call the orchestrator or retriever DIRECTLY with a caller-supplied collection —
/// <see cref="DocumentSearchTool"/> (reachable by MCP clients and prompt-injected agents),
/// <see cref="RetrievalPlanStepExecutor"/>, and <see cref="VectorRetrievalExecutor"/> —
/// cannot escape the tenant-derived collection when the flag is on, because
/// <see cref="RagOrchestrator.SearchAsync"/> and <see cref="HybridRetriever.RetrieveAsync"/>
/// re-derive the collection from the ambient caller identity. Derived names are a pure
/// function of the tenant id and computable offline, so passing a "correct-looking"
/// derived name must not help an attacker either.
/// </summary>
public sealed class ScopedCollectionsChokePointTests
{
    private const string Tenant = "tenant-a";
    private const string VictimCollection = "tenant-victim-0123456789abcdef";

    private static readonly string Derived = ScopedCollectionName.DeriveForTenant(Tenant)!;

    private readonly Mock<IHybridRetriever> _retriever = new();
    private readonly Mock<IReranker> _reranker = new();
    private readonly Mock<ICragEvaluator> _crag = new();
    private readonly Mock<IRagContextAssembler> _assembler = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IBm25Store> _bm25Store = new();
    private readonly Mock<IEmbeddingService> _embedding = new();

    /// <summary>Ambient bridge whose request scope resolves the given tenant.</summary>
    private static IAmbientRequestScope AmbientScopeFor(string? tenantId)
    {
        var scope = new Mock<IKnowledgeScope>();
        scope.SetupGet(s => s.TenantId).Returns(tenantId);
        var provider = new ServiceCollection()
            .AddSingleton(scope.Object)
            .BuildServiceProvider();
        return new FakeAmbientRequestScope(provider);
    }

    private sealed class FakeAmbientRequestScope(IServiceProvider provider) : IAmbientRequestScope
    {
        public IServiceProvider? Current => provider;

        public IDisposable BeginScope(IServiceProvider requestServices) => new NoOpToken();

        private sealed class NoOpToken : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static IOptionsMonitor<AppConfig> ScopedConfig(bool enabled = true) =>
        RagTestData.CreateConfigMonitor(cfg =>
        {
            cfg.AI.Rag.ScopedCollections.Enabled = enabled;
            cfg.AI.Rag.VectorStore.Provider = "faiss";
            cfg.AI.ModelRouting.Enabled = false;
        });

    private RagOrchestrator CreateOrchestrator(
        IOptionsMonitor<AppConfig> config, string? ambientTenant = Tenant)
    {
        _retriever
            .Setup(r => r.RetrieveAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(2));
        SetupPostRetrievalPipeline();

        return new RagOrchestrator(
            _retriever.Object,
            _reranker.Object,
            _crag.Object,
            _assembler.Object,
            graphRagService: null,
            feedbackScorer: null,
            new QueryRouter(
                Mock.Of<IQueryClassifier>(), Mock.Of<IServiceProvider>(), config,
                Mock.Of<ILogger<QueryRouter>>()),
            multiSourceOrchestrator: null,
            complexityClassifier: null,
            costTracker: null,
            config,
            Mock.Of<ILogger<RagOrchestrator>>(),
            ambientScope: AmbientScopeFor(ambientTenant));
    }

    private HybridRetriever CreateHybridRetriever(
        IOptionsMonitor<AppConfig> config, string? ambientTenant = Tenant)
    {
        _embedding
            .Setup(e => e.EmbedQueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadOnlyMemory<float>([0.1f, 0.2f]));
        _vectorStore
            .Setup(v => v.SearchAsync(
                It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _bm25Store
            .Setup(b => b.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        return new HybridRetriever(
            _vectorStore.Object,
            _bm25Store.Object,
            _embedding.Object,
            config,
            Mock.Of<ILogger<HybridRetriever>>(),
            AmbientScopeFor(ambientTenant));
    }

    private void SetupPostRetrievalPipeline()
    {
        _reranker
            .Setup(r => r.RerankAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRerankedResults(2));
        _crag
            .Setup(e => e.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateAcceptEvaluation());
        _assembler
            .Setup(a => a.AssembleAsync(
                It.IsAny<IReadOnlyList<RerankedResult>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext
            {
                AssembledText = "assembled",
                TotalTokens = 10,
                WasTruncated = false,
            });
    }

    private void VerifyRetrieverSawOnlyDerivedCollection()
    {
        _retriever.Verify(r => r.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), Derived, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _retriever.Verify(r => r.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), VictimCollection, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_ScopingOnWithCallerSuppliedCollection_UsesDerivedCollection()
    {
        var orchestrator = CreateOrchestrator(ScopedConfig());

        await orchestrator.SearchAsync("query", 5, VictimCollection);

        VerifyRetrieverSawOnlyDerivedCollection();
    }

    [Fact]
    public async Task SearchAsync_ScopingOnWithAlreadyDerivedName_ResolutionIsIdempotent()
    {
        var orchestrator = CreateOrchestrator(ScopedConfig());

        // The MediatR handler resolves before calling in; the orchestrator resolves again.
        await orchestrator.SearchAsync("query", 5, Derived);

        VerifyRetrieverSawOnlyDerivedCollection();
    }

    [Fact]
    public async Task SearchAsync_ScopingOnWithoutAmbientTenant_FallsToGlobalDefaultCollection()
    {
        var orchestrator = CreateOrchestrator(ScopedConfig(), ambientTenant: null);

        await orchestrator.SearchAsync("query", 5, VictimCollection);

        _retriever.Verify(r => r.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "a caller with no ambient identity must be closed into the global collection, " +
            "never into the collection it named");
    }

    [Fact]
    public async Task SearchAsync_ScopingOff_PassesCallerSuppliedCollectionUnchanged()
    {
        var orchestrator = CreateOrchestrator(ScopedConfig(enabled: false));

        await orchestrator.SearchAsync("query", 5, "corpus-a");

        _retriever.Verify(r => r.RetrieveAsync(
            It.IsAny<string>(), It.IsAny<int>(), "corpus-a", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce, "flag-off behavior must be byte-for-byte unchanged");
    }

    [Fact]
    public async Task DocumentSearchTool_ScopingOnWithCollectionParameter_CannotEscapeDerivedCollection()
    {
        var tool = new DocumentSearchTool(CreateOrchestrator(ScopedConfig()), Mock.Of<ILogger<DocumentSearchTool>>());

        var result = await tool.ExecuteAsync("search", new Dictionary<string, object?>
        {
            ["query"] = "query",
            ["collection"] = VictimCollection,
        });

        result.Success.Should().BeTrue();
        VerifyRetrieverSawOnlyDerivedCollection();
    }

    [Fact]
    public async Task RetrievalPlanStepExecutor_ScopingOnWithConfiguredCollection_CannotEscapeDerivedCollection()
    {
        var executor = new RetrievalPlanStepExecutor(
            CreateOrchestrator(ScopedConfig()),
            Mock.Of<IMultiSourceOrchestrator>(),
            Mock.Of<ITaskComplexityClassifier>(),
            new RetrievalCostTracker(RagTestData.CreateConfigMonitor()),
            Mock.Of<IPlanProgressNotifier>(),
            // Ungoverned: no envelope armed and every gate off, so the chain admits.
            PermissiveAdmission.Pipeline(),
            new PlanExecutionContext(),
            Mock.Of<ILogger<RetrievalPlanStepExecutor>>());

        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Scoped retrieval",
            Type = StepType.Retrieval,
            Configuration = new RetrievalStepConfiguration
            {
                Query = "query",
                TopK = 5,
                CollectionName = VictimCollection,
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 1 },
        };

        var result = await executor.ExecuteAsync(
            step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        result.Status.Should().Be(StepExecutionStatus.Completed);
        VerifyRetrieverSawOnlyDerivedCollection();
    }

    [Fact]
    public async Task VectorRetrievalExecutor_ScopingOnWithMessageCollection_CannotEscapeDerivedCollection()
    {
        var hybridRetriever = CreateHybridRetriever(ScopedConfig());
        var executor = new VectorRetrievalExecutor(
            hybridRetriever,
            _reranker.Object,
            _crag.Object,
            feedbackScorer: null,
            Mock.Of<ILogger<VectorRetrievalExecutor>>());

        await executor.HandleAsync(
            new ClassifiedQuery("query", 5, VictimCollection, RetrievalStrategy.HybridVectorBm25),
            Mock.Of<IWorkflowContext>());

        _vectorStore.Verify(v => v.SearchAsync(
            It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), Derived, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _vectorStore.Verify(v => v.SearchAsync(
            It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<int>(), VictimCollection,
            It.IsAny<CancellationToken>()),
            Times.Never);
        _bm25Store.Verify(b => b.SearchAsync(
            It.IsAny<string>(), It.IsAny<int>(), VictimCollection, It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

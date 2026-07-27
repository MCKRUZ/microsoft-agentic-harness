using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class RetrievalPlanStepExecutorTests
{
    private readonly Mock<IRagOrchestrator> _mockRagOrchestrator = new();
    private readonly Mock<IMultiSourceOrchestrator> _mockMultiSource = new();
    private readonly Mock<ITaskComplexityClassifier> _mockComplexityClassifier = new();
    private readonly Mock<IRetrievalCostTracker> _mockCostTracker = new();
    private readonly Mock<IPlanProgressNotifier> _mockNotifier = new();
    private readonly PlanExecutionContext _context = new() { CurrentPlanId = new PlanId(Guid.NewGuid()) };

    private readonly RagAssembledContext _expectedContext = new()
    {
        AssembledText = "Retrieved context about clean architecture.",
        TotalTokens = 150,
        WasTruncated = false,
        Citations =
        [
            new CitationSpan
            {
                ChunkId = "chunk-1",
                DocumentUri = new Uri("file:///docs/arch.md"),
                SectionPath = "Architecture > Overview",
                StartOffset = 0,
                EndOffset = 44
            }
        ]
    };

    private readonly Mock<IToolInvocationGovernor> _governor = new();

    public RetrievalPlanStepExecutorTests()
    {
        // Ungoverned default: no envelope armed and enforcement off, so the governor allows.
        GovernorReturns(allowed: true);
    }

    private RetrievalPlanStepExecutor CreateExecutor()
    {
        return new RetrievalPlanStepExecutor(
            _mockRagOrchestrator.Object,
            _mockMultiSource.Object,
            _mockComplexityClassifier.Object,
            _mockCostTracker.Object,
            _mockNotifier.Object,
            _governor.Object,
            _context,
            NullLogger<RetrievalPlanStepExecutor>.Instance);
    }

    /// <summary>Arms the governor to allow (the ungoverned default) or deny retrieval.</summary>
    private void GovernorReturns(bool allowed) =>
        _governor.Setup(g => g.AuthorizeAsync(PlanCapabilities.Retrieval, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(allowed
                ? ToolInvocationDecision.Allow()
                : ToolInvocationDecision.Deny(GovernanceDenials.NotPermitted(PlanCapabilities.Retrieval))));

    private static PlanStep CreateRetrievalStep(RetrievalStepConfiguration config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "Retrieve context",
        Type = StepType.Retrieval,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_GovernorDenies_FailsWithoutQuerying()
    {
        // A denial must fail the step closed before any orchestrator is touched, and must be marked
        // as a policy denial so the scheduler routes it to a terminal failure the plan's own
        // OnExhausted policy cannot soften.
        GovernorReturns(allowed: false);
        var step = CreateRetrievalStep(new RetrievalStepConfiguration { Query = "confidential lookup" });

        var result = await CreateExecutor().ExecuteAsync(
            step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.True(result.IsPolicyDenial);
        Assert.Contains("not permitted", result.ErrorMessage);
        _mockRagOrchestrator.Verify(r => r.SearchAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<Domain.AI.RAG.Enums.RetrievalStrategy?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AuthorizesUnderTheWellKnownRetrievalCapability()
    {
        // The name matters: it is what the envelope's allowlist and autonomy-ceiling baseline key on.
        _mockRagOrchestrator
            .Setup(r => r.SearchAsync("What is clean architecture?", null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);
        var step = CreateRetrievalStep(new RetrievalStepConfiguration { Query = "What is clean architecture?" });

        var result = await CreateExecutor().ExecuteAsync(
            step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        _governor.Verify(g => g.AuthorizeAsync("rag_retrieval", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_BasicRetrieval_CallsOrchestrator()
    {
        var config = new RetrievalStepConfiguration { Query = "What is clean architecture?" };
        var step = CreateRetrievalStep(config);

        _mockRagOrchestrator
            .Setup(r => r.SearchAsync("What is clean architecture?", null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var sut = CreateExecutor();
        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.Output);
        Assert.True(result.Duration > TimeSpan.Zero);

        _mockRagOrchestrator.Verify(
            r => r.SearchAsync("What is clean architecture?", null, null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultiSource_CallsMultiSourceOrchestrator()
    {
        var config = new RetrievalStepConfiguration { Query = "Multi-hop query", UseMultiSource = true };
        var step = CreateRetrievalStep(config);

        _mockComplexityClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskComplexityAssessment
            {
                Complexity = TaskComplexity.Complex,
                Confidence = 0.9,
                Source = ClassificationSource.LlmClassifier,
                Reasoning = string.Empty,
            });

        _mockMultiSource
            .Setup(m => m.RetrieveFromAllSourcesAsync("Multi-hop query", 10, TaskComplexity.Complex, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RetrievalResult>
            {
                new()
                {
                    Chunk = new DocumentChunk
                    {
                        Id = "chunk-1",
                        DocumentId = "doc-1",
                        SectionPath = "Test",
                        Content = "Multi-source result",
                        Tokens = 5,
                        Metadata = new ChunkMetadata
                        {
                            SourceUri = new Uri("file:///test.md"),
                            CreatedAt = DateTimeOffset.UtcNow
                        }
                    },
                    DenseScore = 0.9,
                    SparseScore = 0.8,
                    FusedScore = 0.85
                }
            });

        var sut = CreateExecutor();
        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);

        _mockMultiSource.Verify(
            m => m.RetrieveFromAllSourcesAsync("Multi-hop query", 10, TaskComplexity.Complex, null, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockRagOrchestrator.Verify(
            r => r.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithStrategyOverride_PassesToOrchestrator()
    {
        var config = new RetrievalStepConfiguration
        {
            Query = "RAPTOR query",
            Strategy = RetrievalStrategy.RaptorTree,
            TopK = 5,
            CollectionName = "special-index"
        };
        var step = CreateRetrievalStep(config);

        _mockRagOrchestrator
            .Setup(r => r.SearchAsync("RAPTOR query", 5, "special-index", RetrievalStrategy.RaptorTree, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var sut = CreateExecutor();
        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);

        _mockRagOrchestrator.Verify(
            r => r.SearchAsync("RAPTOR query", 5, "special-index", RetrievalStrategy.RaptorTree, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_OrchestratorFails_ReturnsFailedResult()
    {
        var config = new RetrievalStepConfiguration { Query = "failing query" };
        var step = CreateRetrievalStep(config);

        _mockRagOrchestrator
            .Setup(r => r.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Vector store https://acct.search.windows.net?sig=SECRET unavailable"));

        var sut = CreateExecutor();
        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        // MED-2: step error state is persisted and returned to callers, so retrieval-stack exception
        // text — which carries store endpoints and SAS tokens — must never reach it.
        Assert.Equal(PlanStepErrors.RetrievalFailed, result.ErrorMessage);
        Assert.DoesNotContain("SECRET", result.ErrorMessage);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_TracksRetrievalCost()
    {
        var config = new RetrievalStepConfiguration { Query = "cost tracking query" };
        var step = CreateRetrievalStep(config);

        _mockRagOrchestrator
            .Setup(r => r.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var sut = CreateExecutor();
        await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        _mockCostTracker.Verify(
            t => t.RecordCall(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SerializesContextAsOutput()
    {
        var config = new RetrievalStepConfiguration { Query = "serialize test" };
        var step = CreateRetrievalStep(config);

        _mockRagOrchestrator
            .Setup(r => r.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_expectedContext);

        var sut = CreateExecutor();
        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.Output);

        using var doc = JsonDocument.Parse(result.Output!);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("assembledText", out var assembledText));
        Assert.Equal("Retrieved context about clean architecture.", assembledText.GetString());

        Assert.True(root.TryGetProperty("totalTokens", out var totalTokens));
        Assert.Equal(150, totalTokens.GetInt32());
    }
}

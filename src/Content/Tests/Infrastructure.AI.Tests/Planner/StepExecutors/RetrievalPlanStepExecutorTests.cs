using System.Text.Json;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.Planner;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class RetrievalPlanStepExecutorTests
{
    private readonly Mock<IRagOrchestrator> _mockRagOrchestrator = new();
    private readonly Mock<IMultiSourceOrchestrator> _mockMultiSource = new();
    private readonly Mock<IQueryComplexityClassifier> _mockComplexityClassifier = new();
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

    private RetrievalPlanStepExecutor CreateExecutor()
    {
        return new RetrievalPlanStepExecutor(
            _mockRagOrchestrator.Object,
            _mockMultiSource.Object,
            _mockComplexityClassifier.Object,
            _mockCostTracker.Object,
            _mockNotifier.Object,
            _context,
            NullLogger<RetrievalPlanStepExecutor>.Instance);
    }

    private static PlanStep CreateRetrievalStep(RetrievalStepConfiguration config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "Retrieve context",
        Type = StepType.Retrieval,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

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
            .Setup(c => c.ClassifyAsync("Multi-hop query", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplexityClassification { Complexity = QueryComplexity.Complex, Confidence = 0.9 });

        _mockMultiSource
            .Setup(m => m.RetrieveFromAllSourcesAsync("Multi-hop query", 10, QueryComplexity.Complex, It.IsAny<CancellationToken>()))
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
            m => m.RetrieveFromAllSourcesAsync("Multi-hop query", 10, QueryComplexity.Complex, It.IsAny<CancellationToken>()),
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
            .ThrowsAsync(new InvalidOperationException("Vector store unavailable"));

        var sut = CreateExecutor();
        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("Vector store unavailable", result.ErrorMessage);
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

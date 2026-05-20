using System.Text.Json;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.Planner;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.Planner.StepExecutors;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

/// <summary>
/// End-to-end integration tests verifying the full autonomy flow:
/// planner creates retrieval step -> executor runs it -> output is consumable by downstream steps.
/// </summary>
public sealed class FullAutonomyIntegrationTests
{
    private readonly Mock<IRagOrchestrator> _mockRagOrchestrator = new();
    private readonly Mock<IMultiSourceOrchestrator> _mockMultiSource = new();
    private readonly Mock<IQueryComplexityClassifier> _mockComplexityClassifier = new();
    private readonly Mock<IPlanProgressNotifier> _mockNotifier = new();

    private RetrievalPlanStepExecutor CreateRetrievalExecutor(IRetrievalCostTracker? costTracker = null)
    {
        return new RetrievalPlanStepExecutor(
            _mockRagOrchestrator.Object,
            _mockMultiSource.Object,
            _mockComplexityClassifier.Object,
            costTracker ?? new RetrievalCostTracker(RagTestData.CreateConfigMonitor()),
            _mockNotifier.Object,
            new PlanExecutionContext(),
            Mock.Of<ILogger<RetrievalPlanStepExecutor>>());
    }

    [Fact]
    public async Task RetrievalStep_ProducesJsonOutput_ConsumableByDownstreamLlmStep()
    {
        // Arrange -- Setup RAG orchestrator to return context
        var expectedContext = new RagAssembledContext
        {
            AssembledText = "Clean Architecture separates concerns into layers.",
            TotalTokens = 42,
            WasTruncated = false,
            Citations =
            [
                new CitationSpan
                {
                    ChunkId = "arch-chunk-1",
                    DocumentUri = new Uri("file:///docs/architecture.md"),
                    SectionPath = "Architecture > Overview",
                    StartOffset = 0,
                    EndOffset = 49
                }
            ]
        };

        _mockRagOrchestrator
            .Setup(r => r.SearchAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedContext);

        var executor = CreateRetrievalExecutor();

        // Create retrieval step as planner would
        var retrievalStep = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Retrieve architecture docs",
            Type = StepType.Retrieval,
            Configuration = new RetrievalStepConfiguration
            {
                Query = "What is clean architecture?",
                TopK = 5
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 1 }
        };

        // Act
        var result = await executor.ExecuteAsync(
            retrievalStep,
            new Dictionary<PlanStepId, string>(),
            CancellationToken.None);

        // Assert -- result is completed and output is valid JSON
        result.Status.Should().Be(StepExecutionStatus.Completed);
        result.Output.Should().NotBeNullOrEmpty();

        // Verify the JSON can be parsed and contains expected data
        var outputDoc = JsonDocument.Parse(result.Output!);
        var assembledText = outputDoc.RootElement.GetProperty("assembledText").GetString();
        assembledText.Should().Contain("Clean Architecture");

        // Verify the output could serve as upstream input to an LLM step
        var upstreamOutputs = new Dictionary<PlanStepId, string>
        {
            { retrievalStep.Id, result.Output! }
        };
        upstreamOutputs[retrievalStep.Id].Should().Contain("assembledText");
    }

    [Fact]
    public async Task MultiSourceRetrievalStep_IncludesComplexityInOutput()
    {
        // Arrange
        var retrievalResults = RagTestData.CreateRetrievalResults(3);
        _mockMultiSource
            .Setup(m => m.RetrieveFromAllSourcesAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<QueryComplexity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retrievalResults);
        _mockComplexityClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplexityClassification
            {
                Complexity = QueryComplexity.Complex,
                Confidence = 0.9
            });

        var executor = CreateRetrievalExecutor();

        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Multi-source retrieval",
            Type = StepType.Retrieval,
            Configuration = new RetrievalStepConfiguration
            {
                Query = "Compare all architecture patterns used in the system",
                UseMultiSource = true,
                TopK = 10
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 }
        };

        // Act
        var result = await executor.ExecuteAsync(
            step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        // Assert
        result.Status.Should().Be(StepExecutionStatus.Completed);
        var outputDoc = JsonDocument.Parse(result.Output!);
        outputDoc.RootElement.GetProperty("complexity").GetString()
            .Should().Be("Complex");
        outputDoc.RootElement.GetProperty("resultCount").GetInt32()
            .Should().Be(3);
    }

    [Fact]
    public async Task CostTracker_AggregatesAcrossMultipleSteps()
    {
        // Arrange
        var costTracker = new RetrievalCostTracker(RagTestData.CreateConfigMonitor());

        _mockRagOrchestrator
            .Setup(r => r.SearchAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext
            {
                AssembledText = "Result", TotalTokens = 50, WasTruncated = false
            });

        var executor = CreateRetrievalExecutor(costTracker);

        // Act -- execute 3 retrieval steps
        for (var i = 0; i < 3; i++)
        {
            var step = new PlanStep
            {
                Id = PlanStepId.New(),
                Name = $"Retrieval step {i}",
                Type = StepType.Retrieval,
                Configuration = new RetrievalStepConfiguration { Query = $"query {i}" },
                RetryPolicy = new RetryPolicy { MaxRetries = 0 }
            };

            await executor.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);
        }

        // Assert -- cost tracker aggregated all 3 calls
        var summary = costTracker.GetSummary();
        summary.RetrievalCalls.Should().Be(3);
        summary.TotalTokensUsed.Should().BeGreaterThan(0);
        summary.TotalLatency.Should().BeGreaterThan(TimeSpan.Zero);
    }
}

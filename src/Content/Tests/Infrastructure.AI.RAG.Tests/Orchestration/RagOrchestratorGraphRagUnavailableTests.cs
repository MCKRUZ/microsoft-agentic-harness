using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.QueryTransform;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

/// <summary>
/// Verifies that <see cref="RagOrchestrator"/> degrades the GraphRag strategy honestly when
/// <c>IGraphRagService</c> is absent (the graph database backend is disabled): an explanatory
/// context naming the disabled config, never an exception. Mirrors the established
/// empty-graph behavior of <c>ManagedCodeGraphRagService.GlobalSearchAsync</c>.
/// </summary>
public sealed class RagOrchestratorGraphRagUnavailableTests
{
    private static RagOrchestrator CreateOrchestratorWithoutGraphRag()
    {
        var config = RagTestData.CreateConfigMonitor();
        var queryRouter = new QueryRouter(
            Mock.Of<IQueryClassifier>(),
            Mock.Of<IServiceProvider>(),
            config,
            Mock.Of<ILogger<QueryRouter>>());

        return new RagOrchestrator(
            Mock.Of<IHybridRetriever>(),
            Mock.Of<IReranker>(),
            Mock.Of<ICragEvaluator>(),
            Mock.Of<IRagContextAssembler>(),
            graphRagService: null,
            feedbackScorer: null,
            queryRouter,
            multiSourceOrchestrator: null,
            complexityClassifier: null,
            costTracker: null,
            config,
            Mock.Of<ILogger<RagOrchestrator>>());
    }

    [Fact]
    public async Task SearchAsync_GraphRagOverrideWithoutBackend_ReturnsExplanatoryContext()
    {
        var orchestrator = CreateOrchestratorWithoutGraphRag();

        var result = await orchestrator.SearchAsync(
            "What are the main themes?",
            strategyOverride: RetrievalStrategy.GraphRag);

        result.AssembledText.Should().Contain("GraphRAG retrieval is unavailable")
            .And.Contain("AppConfig:AI:Rag:GraphDatabase:Enabled",
                "the degraded context must name the config knob that re-enables the feature");
        result.TotalTokens.Should().Be(0);
        result.WasTruncated.Should().BeFalse();
    }

    [Fact]
    public void Constructor_NullGraphRagService_DoesNotThrow()
    {
        var act = CreateOrchestratorWithoutGraphRag;

        act.Should().NotThrow(
            "the orchestrator must compose in hosts that run with the graph database disabled");
    }
}

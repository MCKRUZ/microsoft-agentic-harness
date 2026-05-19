using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

public sealed class RetrievalDecisionGateTests
{
    private RetrievalDecisionGate CreateGate(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(configure);
        return new RetrievalDecisionGate(
            config,
            Mock.Of<ILogger<RetrievalDecisionGate>>());
    }

    [Fact]
    public void Decide_TrivialQuery_SkipsRetrieval()
    {
        var gate = CreateGate();
        var classification = RagTestData.CreateTrivialClassification();

        var decision = gate.Decide(classification);

        decision.SkipRetrieval.Should().BeTrue();
        decision.Complexity.Should().Be(QueryComplexity.Trivial);
    }

    [Fact]
    public void Decide_SimpleQuery_UsesLightweightRetrieval()
    {
        var gate = CreateGate();
        var classification = RagTestData.CreateSimpleClassification();

        var decision = gate.Decide(classification);

        decision.SkipRetrieval.Should().BeFalse();
        decision.TopK.Should().Be(5);
        decision.UseReranking.Should().BeFalse();
        decision.UseCragEvaluation.Should().BeFalse();
        decision.Complexity.Should().Be(QueryComplexity.Simple);
    }

    [Fact]
    public void Decide_ModerateQuery_UsesFullPipeline()
    {
        var gate = CreateGate();
        var classification = RagTestData.CreateModerateClassification();

        var decision = gate.Decide(classification);

        decision.SkipRetrieval.Should().BeFalse();
        decision.TopK.Should().Be(10); // ModerateTopK is null in test config; falls back to Retrieval.TopK = 10
        decision.UseReranking.Should().BeTrue();
        decision.UseCragEvaluation.Should().BeTrue();
        decision.Complexity.Should().Be(QueryComplexity.Moderate);
    }

    [Fact]
    public void Decide_ComplexQuery_UsesEnhancedRetrieval()
    {
        var gate = CreateGate();
        var classification = RagTestData.CreateComplexClassification();

        var decision = gate.Decide(classification);

        decision.SkipRetrieval.Should().BeFalse();
        decision.TopK.Should().Be(15);
        decision.UseReranking.Should().BeTrue();
        decision.UseCragEvaluation.Should().BeTrue();
        decision.Complexity.Should().Be(QueryComplexity.Complex);
    }

    [Fact]
    public void Decide_LowConfidence_FallsBackToModerate()
    {
        var gate = CreateGate();
        var classification = new ComplexityClassification
        {
            Complexity = QueryComplexity.Trivial,
            Confidence = 0.4,
            Reasoning = "Low confidence classification",
        };

        var decision = gate.Decide(classification);

        decision.SkipRetrieval.Should().BeFalse();
        decision.UseReranking.Should().BeTrue();
        decision.UseCragEvaluation.Should().BeTrue();
        decision.Complexity.Should().Be(QueryComplexity.Moderate);
    }

    [Fact]
    public void Decide_CallerOverridesTopK_UsesCallerValue()
    {
        var gate = CreateGate();
        var classification = RagTestData.CreateSimpleClassification();

        var decision = gate.Decide(classification, requestedTopK: 20);

        decision.TopK.Should().Be(20);
    }

    [Fact]
    public void Decide_RoutingDisabled_AlwaysReturnsFullPipeline()
    {
        var gate = CreateGate(c => c.AI.Rag.ComplexityRouting.Enabled = false);
        var classification = RagTestData.CreateTrivialClassification();

        var decision = gate.Decide(classification);

        decision.SkipRetrieval.Should().BeFalse();
        decision.UseReranking.Should().BeTrue();
        decision.UseCragEvaluation.Should().BeTrue();
    }
}

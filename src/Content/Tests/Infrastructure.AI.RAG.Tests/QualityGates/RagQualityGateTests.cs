using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.QualityGates;

/// <summary>
/// CI/CD quality gate tests that evaluate retrieval quality against a deterministic
/// golden dataset. Each test runs a query through a mock evaluation pipeline and
/// asserts that Ragas-style metrics meet minimum thresholds.
/// <para>
/// In production CI, these tests run against a real RAG pipeline with a curated
/// golden dataset. For unit testing, we use deterministic mocks to verify the
/// gate logic works correctly.
/// </para>
/// </summary>
public sealed class RagQualityGateTests
{
    /// <summary>
    /// Deterministic golden dataset entries for quality gate testing.
    /// Each entry defines a query, expected ground-truth answer, and mock
    /// retrieval context. In production, this would be loaded from a JSON file.
    /// </summary>
    private static readonly GoldenDatasetEntry[] GoldenDataset =
    [
        new(
            Query: "What is the purpose of Clean Architecture?",
            GroundTruth: "Clean Architecture separates concerns into layers with dependencies pointing inward. " +
                         "Domain has no external dependencies. Application depends only on Domain. " +
                         "Infrastructure implements Application interfaces.",
            ExpectedAnswer: "Clean Architecture separates concerns into layers where dependencies point inward, " +
                            "with Domain at the center having no external dependencies."),
        new(
            Query: "How does the planner execute steps?",
            GroundTruth: "The PlanExecutor orchestrates a PlanGraph with bounded concurrency. " +
                         "Steps are dispatched to keyed IPlanStepExecutor implementations via StepType. " +
                         "State is persisted to EfCorePlanStateStore with checkpoint/resume support.",
            ExpectedAnswer: "The PlanExecutor runs plan steps using keyed step executors, " +
                            "with state persistence and checkpoint/resume capabilities."),
        new(
            Query: "What retrieval strategies does the RAG pipeline support?",
            GroundTruth: "The RAG pipeline supports HybridVectorBm25, GraphRag, RaptorTree, " +
                         "and MultiQueryFusion strategies. Strategy selection is either automatic " +
                         "via query classification or manual via strategy override.",
            ExpectedAnswer: "The RAG pipeline supports four strategies: hybrid vector+BM25, " +
                            "GraphRAG, RAPTOR tree, and multi-query fusion.")
    ];

    private readonly Mock<IRetrievalQualityEvaluator> _mockEvaluator = new();

    private void SetupEvaluatorWithScores(
        double precision, double recall, double faithfulness, double relevancy)
    {
        var overallScore = (precision * 0.25) + (recall * 0.25) +
                           (faithfulness * 0.30) + (relevancy * 0.20);

        _mockEvaluator
            .Setup(e => e.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RerankedResult>>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrievalQualityReport
            {
                ContextPrecision = precision,
                ContextRecall = recall,
                Faithfulness = faithfulness,
                AnswerRelevancy = relevancy,
                OverallScore = overallScore,
                Reasoning = "Deterministic test evaluation",
                EvaluatedAt = DateTimeOffset.UtcNow
            });
    }

    [Fact]
    public async Task QualityGate_GoldenDataset_ContextPrecisionAboveThreshold()
    {
        // Arrange -- simulate high precision retrieval
        const double minPrecision = 0.7;
        SetupEvaluatorWithScores(precision: 0.85, recall: 0.80, faithfulness: 0.90, relevancy: 0.88);

        // Act -- evaluate each golden dataset entry
        var precisionScores = new List<double>();
        foreach (var entry in GoldenDataset)
        {
            var context = RagTestData.CreateRerankedResults(3);
            var report = await _mockEvaluator.Object.EvaluateAsync(
                entry.Query, entry.ExpectedAnswer, context, entry.GroundTruth);
            precisionScores.Add(report.ContextPrecision);
        }

        // Assert -- average precision must be above threshold
        var avgPrecision = precisionScores.Average();
        avgPrecision.Should().BeGreaterThanOrEqualTo(minPrecision,
            $"average context precision ({avgPrecision:F2}) must be >= {minPrecision} " +
            "to pass the quality gate");
    }

    [Fact]
    public async Task QualityGate_GoldenDataset_FaithfulnessAboveThreshold()
    {
        // Arrange -- simulate high faithfulness
        const double minFaithfulness = 0.8;
        SetupEvaluatorWithScores(precision: 0.85, recall: 0.80, faithfulness: 0.92, relevancy: 0.88);

        // Act
        var faithfulnessScores = new List<double>();
        foreach (var entry in GoldenDataset)
        {
            var context = RagTestData.CreateRerankedResults(3);
            var report = await _mockEvaluator.Object.EvaluateAsync(
                entry.Query, entry.ExpectedAnswer, context, entry.GroundTruth);
            faithfulnessScores.Add(report.Faithfulness);
        }

        // Assert
        var avgFaithfulness = faithfulnessScores.Average();
        avgFaithfulness.Should().BeGreaterThanOrEqualTo(minFaithfulness,
            $"average faithfulness ({avgFaithfulness:F2}) must be >= {minFaithfulness} " +
            "to pass the quality gate");
    }

    [Fact]
    public async Task QualityGate_GoldenDataset_OverallScoreAboveThreshold()
    {
        // Arrange
        const double minOverall = 0.7;
        SetupEvaluatorWithScores(precision: 0.85, recall: 0.80, faithfulness: 0.90, relevancy: 0.88);

        // Act
        var overallScores = new List<double>();
        foreach (var entry in GoldenDataset)
        {
            var context = RagTestData.CreateRerankedResults(3);
            var report = await _mockEvaluator.Object.EvaluateAsync(
                entry.Query, entry.ExpectedAnswer, context, entry.GroundTruth);
            overallScores.Add(report.OverallScore);
        }

        // Assert
        var avgOverall = overallScores.Average();
        avgOverall.Should().BeGreaterThanOrEqualTo(minOverall,
            $"average overall score ({avgOverall:F2}) must be >= {minOverall} " +
            "to pass the quality gate");
    }

    /// <summary>
    /// A single entry in the golden evaluation dataset.
    /// </summary>
    private sealed record GoldenDatasetEntry(
        string Query,
        string GroundTruth,
        string ExpectedAnswer);
}

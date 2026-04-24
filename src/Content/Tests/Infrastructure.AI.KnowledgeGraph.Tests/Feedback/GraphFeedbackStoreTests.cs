using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Feedback;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Feedback;

/// <summary>
/// Tests for <see cref="GraphFeedbackStore"/> — EMA weight updates,
/// default weights, batch retrieval, and normalization.
/// </summary>
public sealed class GraphFeedbackStoreTests
{
    private readonly GraphFeedbackStore _store;
    private readonly FakeTimeProvider _timeProvider;

    public GraphFeedbackStoreTests()
    {
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero));
        _store = new GraphFeedbackStore(
            Mock.Of<ILogger<GraphFeedbackStore>>(),
            _timeProvider);
    }

    [Fact]
    public async Task GetNodeWeight_NoFeedback_ReturnsDefault()
    {
        var weight = await _store.GetNodeWeightAsync("n1");

        weight.NodeId.Should().Be("n1");
        weight.Weight.Should().Be(1.0);
        weight.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task GetEdgeWeight_NoFeedback_ReturnsDefault()
    {
        var weight = await _store.GetEdgeWeightAsync("e1");

        weight.EdgeId.Should().Be("e1");
        weight.Weight.Should().Be(1.0);
        weight.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyNodeFeedback_FirstTime_SetsNormalizedScore()
    {
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 5.0, alpha: 0.3);

        var weight = await _store.GetNodeWeightAsync("n1");
        weight.Weight.Should().Be(1.0); // (5-1)/4 = 1.0
        weight.UpdateCount.Should().Be(1);
    }

    [Fact]
    public async Task ApplyNodeFeedback_SecondTime_AppliesEma()
    {
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 5.0, alpha: 0.3);
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 1.0, alpha: 0.3);

        var weight = await _store.GetNodeWeightAsync("n1");
        // First: 1.0 (normalized 5 = 1.0)
        // Second: 0.3 * 0.0 + 0.7 * 1.0 = 0.7 (normalized 1 = 0.0)
        weight.Weight.Should().BeApproximately(0.7, 0.001);
        weight.UpdateCount.Should().Be(2);
    }

    [Fact]
    public async Task ApplyNodeFeedback_MiddleScore_NormalizesCorrectly()
    {
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 3.0, alpha: 1.0);

        var weight = await _store.GetNodeWeightAsync("n1");
        // alpha=1.0 means fully replace: normalized 3 = (3-1)/4 = 0.5
        weight.Weight.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task ApplyEdgeFeedback_AppliesEma()
    {
        await _store.ApplyEdgeFeedbackAsync("e1", feedbackScore: 5.0, alpha: 0.5);
        await _store.ApplyEdgeFeedbackAsync("e1", feedbackScore: 3.0, alpha: 0.5);

        var weight = await _store.GetEdgeWeightAsync("e1");
        // First: 1.0
        // Second: 0.5 * 0.5 + 0.5 * 1.0 = 0.75
        weight.Weight.Should().BeApproximately(0.75, 0.001);
        weight.UpdateCount.Should().Be(2);
    }

    [Fact]
    public async Task GetNodeWeightsBatch_ReturnsMixOfExistingAndDefaults()
    {
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 5.0, alpha: 1.0);

        var weights = await _store.GetNodeWeightsBatchAsync(["n1", "n2"]);

        weights.Should().HaveCount(2);
        weights["n1"].Weight.Should().Be(1.0);
        weights["n2"].Weight.Should().Be(1.0);
        weights["n1"].UpdateCount.Should().Be(1);
        weights["n2"].UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyNodeFeedback_SetsTimestamp()
    {
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 3.0, alpha: 0.3);

        var weight = await _store.GetNodeWeightAsync("n1");
        weight.LastUpdatedAt.Should().Be(_timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task ApplyNodeFeedback_ScoreBelowRange_ClampedToZero()
    {
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 0.0, alpha: 1.0);

        var weight = await _store.GetNodeWeightAsync("n1");
        weight.Weight.Should().Be(0.0);
    }

    [Fact]
    public async Task ApplyNodeFeedback_ScoreAboveRange_ClampedToOne()
    {
        await _store.ApplyNodeFeedbackAsync("n1", feedbackScore: 10.0, alpha: 1.0);

        var weight = await _store.GetNodeWeightAsync("n1");
        weight.Weight.Should().Be(1.0);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}

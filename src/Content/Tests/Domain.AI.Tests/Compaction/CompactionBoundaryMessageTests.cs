using Domain.AI.Compaction;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Compaction;

/// <summary>
/// Tests for <see cref="CompactionBoundaryMessage"/> record — construction, computed properties, equality.
/// </summary>
public sealed class CompactionBoundaryMessageTests
{
    [Fact]
    public void TokensSaved_ComputesCorrectDifference()
    {
        var boundary = CreateBoundary(preTokens: 10000, postTokens: 3000);

        boundary.TokensSaved.Should().Be(7000);
    }

    [Fact]
    public void TokensSaved_SamePreAndPost_ReturnsZero()
    {
        var boundary = CreateBoundary(preTokens: 5000, postTokens: 5000);

        boundary.TokensSaved.Should().Be(0);
    }

    [Fact]
    public void TokensSaved_PostLargerThanPre_ReturnsNegative()
    {
        var boundary = CreateBoundary(preTokens: 3000, postTokens: 5000);

        boundary.TokensSaved.Should().Be(-2000);
    }

    [Fact]
    public void Defaults_PreservedSegmentIds_IsEmpty()
    {
        var boundary = CreateBoundary(preTokens: 1000, postTokens: 500);

        boundary.PreservedSegmentIds.Should().BeEmpty();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var segmentIds = new List<string> { "seg-1", "seg-2" };

        var boundary = new CompactionBoundaryMessage
        {
            Id = "compact-001",
            Trigger = CompactionTrigger.AutoBudget,
            Strategy = CompactionStrategy.Full,
            PreCompactionTokens = 15000,
            PostCompactionTokens = 4000,
            PreservedSegmentIds = segmentIds,
            Timestamp = timestamp,
            Summary = "Conversation summarized"
        };

        boundary.Id.Should().Be("compact-001");
        boundary.Trigger.Should().Be(CompactionTrigger.AutoBudget);
        boundary.Strategy.Should().Be(CompactionStrategy.Full);
        boundary.PreCompactionTokens.Should().Be(15000);
        boundary.PostCompactionTokens.Should().Be(4000);
        boundary.TokensSaved.Should().Be(11000);
        boundary.PreservedSegmentIds.Should().BeEquivalentTo(segmentIds);
        boundary.Timestamp.Should().Be(timestamp);
        boundary.Summary.Should().Be("Conversation summarized");
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = CreateBoundary(preTokens: 8000, postTokens: 2000);
        var updated = original with { Strategy = CompactionStrategy.Micro };

        updated.Strategy.Should().Be(CompactionStrategy.Micro);
        original.Strategy.Should().Be(CompactionStrategy.Full);
    }

    private static CompactionBoundaryMessage CreateBoundary(int preTokens, int postTokens) =>
        new()
        {
            Id = "test-boundary",
            Trigger = CompactionTrigger.AutoBudget,
            Strategy = CompactionStrategy.Full,
            PreCompactionTokens = preTokens,
            PostCompactionTokens = postTokens,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "Test summary"
        };
}

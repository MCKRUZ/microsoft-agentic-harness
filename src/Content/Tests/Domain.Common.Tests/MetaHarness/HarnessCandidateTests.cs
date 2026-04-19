using Domain.Common.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

/// <summary>
/// Tests for <see cref="HarnessCandidate"/> record and <see cref="HarnessCandidateStatus"/> enum.
/// </summary>
public class HarnessCandidateTests
{
    private static HarnessSnapshot CreateSnapshot() => new()
    {
        SkillFileSnapshots = new Dictionary<string, string>(),
        SystemPromptSnapshot = "test prompt",
        ConfigSnapshot = new Dictionary<string, string>(),
        SnapshotManifest = []
    };

    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var candidateId = Guid.NewGuid();
        var optRunId = Guid.NewGuid();
        var candidate = new HarnessCandidate
        {
            CandidateId = candidateId,
            OptimizationRunId = optRunId,
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = CreateSnapshot(),
            Status = HarnessCandidateStatus.Proposed
        };

        candidate.CandidateId.Should().Be(candidateId);
        candidate.OptimizationRunId.Should().Be(optRunId);
        candidate.Iteration.Should().Be(0);
        candidate.Status.Should().Be(HarnessCandidateStatus.Proposed);
    }

    [Fact]
    public void OptionalProperties_DefaultCorrectly()
    {
        var candidate = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = CreateSnapshot(),
            Status = HarnessCandidateStatus.Proposed
        };

        candidate.ParentCandidateId.Should().BeNull();
        candidate.BestScore.Should().BeNull();
        candidate.TokenCost.Should().BeNull();
        candidate.FailureReason.Should().BeNull();
    }

    [Fact]
    public void WithExpression_TransitionsStatus()
    {
        var original = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = CreateSnapshot(),
            Status = HarnessCandidateStatus.Proposed
        };

        var evaluated = original with
        {
            Status = HarnessCandidateStatus.Evaluated,
            BestScore = 0.85,
            TokenCost = 5000
        };

        original.Status.Should().Be(HarnessCandidateStatus.Proposed);
        evaluated.Status.Should().Be(HarnessCandidateStatus.Evaluated);
        evaluated.BestScore.Should().Be(0.85);
        evaluated.TokenCost.Should().Be(5000);
    }

    [Fact]
    public void WithExpression_FailedCandidate()
    {
        var candidate = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = CreateSnapshot(),
            Status = HarnessCandidateStatus.Proposed
        };

        var failed = candidate with
        {
            Status = HarnessCandidateStatus.Failed,
            FailureReason = "Eval timed out"
        };

        failed.Status.Should().Be(HarnessCandidateStatus.Failed);
        failed.FailureReason.Should().Be("Eval timed out");
    }
}

/// <summary>
/// Tests for <see cref="HarnessCandidateStatus"/> enum values.
/// </summary>
public class HarnessCandidateStatusTests
{
    [Theory]
    [InlineData(HarnessCandidateStatus.Proposed, 0)]
    [InlineData(HarnessCandidateStatus.Evaluated, 1)]
    [InlineData(HarnessCandidateStatus.Failed, 2)]
    [InlineData(HarnessCandidateStatus.Promoted, 3)]
    public void Value_HasExpectedInteger(HarnessCandidateStatus status, int expected)
    {
        ((int)status).Should().Be(expected);
    }

    [Fact]
    public void AllValues_AreDistinct()
    {
        Enum.GetValues<HarnessCandidateStatus>().Should().OnlyHaveUniqueItems();
    }
}

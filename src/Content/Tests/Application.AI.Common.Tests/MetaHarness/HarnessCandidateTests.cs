using Domain.Common.MetaHarness;
using Xunit;

namespace Application.AI.Common.Tests.MetaHarness;

/// <summary>
/// Tests for HarnessCandidate domain model immutability and HarnessSnapshot integrity.
/// </summary>
public class HarnessCandidateTests
{
    private static HarnessSnapshot BuildSnapshot() => new()
    {
        SkillFileSnapshots = new Dictionary<string, string>
        {
            ["skills/agent/SKILL.md"] = "# Skill",
            ["skills/agent/TOOL.md"] = "# Tool"
        },
        SystemPromptSnapshot = "You are a helpful assistant.",
        ConfigSnapshot = new Dictionary<string, string> { ["Region"] = "eastus" },
        SnapshotManifest =
        [
            new SnapshotEntry("skills/agent/SKILL.md", "abc123"),
            new SnapshotEntry("skills/agent/TOOL.md", "def456")
        ]
    };

    private static HarnessCandidate BuildCandidate(HarnessCandidateStatus status = HarnessCandidateStatus.Proposed) =>
        new()
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = BuildSnapshot(),
            Status = status
        };

    [Fact]
    public void HarnessCandidate_StatusTransition_ProducesNewImmutableRecord()
    {
        var original = BuildCandidate(HarnessCandidateStatus.Proposed);

        var updated = original with { Status = HarnessCandidateStatus.Evaluated };

        Assert.Equal(HarnessCandidateStatus.Proposed, original.Status);
        Assert.Equal(HarnessCandidateStatus.Evaluated, updated.Status);
        Assert.False(ReferenceEquals(original, updated));
    }

    [Fact]
    public void HarnessCandidate_WithExpression_DoesNotMutateOriginal()
    {
        var candidate = BuildCandidate();
        var originalScore = candidate.BestScore;

        var updated = candidate with { BestScore = 0.9, TokenCost = 1000 };

        Assert.Null(candidate.BestScore);
        Assert.Equal(originalScore, candidate.BestScore);
        Assert.Equal(0.9, updated.BestScore);
        Assert.Equal(1000, updated.TokenCost);
    }

    [Fact]
    public void HarnessSnapshot_SnapshotManifest_ContainsHashForEachSkillFile()
    {
        var snapshot = BuildSnapshot();

        Assert.Equal(2, snapshot.SnapshotManifest.Count);
        Assert.All(snapshot.SnapshotManifest, entry =>
        {
            Assert.False(string.IsNullOrEmpty(entry.Sha256Hash));
            Assert.False(string.IsNullOrEmpty(entry.Path));
        });
    }
}

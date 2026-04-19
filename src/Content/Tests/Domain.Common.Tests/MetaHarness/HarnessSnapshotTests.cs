using Domain.Common.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

/// <summary>
/// Tests for <see cref="HarnessSnapshot"/>, <see cref="SnapshotEntry"/>,
/// <see cref="HarnessProposal"/>, <see cref="HarnessProposerContext"/>,
/// <see cref="RunMetadata"/>, and <see cref="TurnArtifacts"/> records.
/// </summary>
public class HarnessSnapshotTests
{
    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var snapshot = new HarnessSnapshot
        {
            SkillFileSnapshots = new Dictionary<string, string> { ["SKILL.md"] = "content" },
            SystemPromptSnapshot = "You are an agent",
            ConfigSnapshot = new Dictionary<string, string> { ["key"] = "val" },
            SnapshotManifest = [new SnapshotEntry("SKILL.md", "abc123")]
        };

        snapshot.SkillFileSnapshots.Should().ContainKey("SKILL.md");
        snapshot.SystemPromptSnapshot.Should().Be("You are an agent");
        snapshot.ConfigSnapshot.Should().ContainKey("key");
        snapshot.SnapshotManifest.Should().ContainSingle();
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new HarnessSnapshot
        {
            SkillFileSnapshots = new Dictionary<string, string>(),
            SystemPromptSnapshot = "original",
            ConfigSnapshot = new Dictionary<string, string>(),
            SnapshotManifest = []
        };

        var modified = original with { SystemPromptSnapshot = "modified" };

        original.SystemPromptSnapshot.Should().Be("original");
        modified.SystemPromptSnapshot.Should().Be("modified");
    }
}

/// <summary>
/// Tests for <see cref="SnapshotEntry"/> positional record.
/// </summary>
public class SnapshotEntryTests
{
    [Fact]
    public void Construction_SetsProperties()
    {
        var entry = new SnapshotEntry("skills/agent/SKILL.md", "abc123def456");

        entry.Path.Should().Be("skills/agent/SKILL.md");
        entry.Sha256Hash.Should().Be("abc123def456");
    }

    [Fact]
    public void Deconstruction_Works()
    {
        var entry = new SnapshotEntry("path.md", "hash123");

        var (path, hash) = entry;

        path.Should().Be("path.md");
        hash.Should().Be("hash123");
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new SnapshotEntry("p", "h");
        var b = new SnapshotEntry("p", "h");

        a.Should().Be(b);
    }
}

/// <summary>
/// Tests for <see cref="HarnessProposal"/> record.
/// </summary>
public class HarnessProposalTests
{
    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var proposal = new HarnessProposal
        {
            ProposedSkillChanges = new Dictionary<string, string>(),
            ProposedConfigChanges = new Dictionary<string, string>(),
            Reasoning = "Better prompts"
        };

        proposal.ProposedSkillChanges.Should().BeEmpty();
        proposal.ProposedConfigChanges.Should().BeEmpty();
        proposal.Reasoning.Should().Be("Better prompts");
    }

    [Fact]
    public void OptionalProperties_DefaultCorrectly()
    {
        var proposal = new HarnessProposal
        {
            ProposedSkillChanges = new Dictionary<string, string>(),
            ProposedConfigChanges = new Dictionary<string, string>(),
            Reasoning = "test"
        };

        proposal.ProposedSystemPromptChange.Should().BeNull();
        proposal.Learnings.Should().BeNull();
    }
}

/// <summary>
/// Tests for <see cref="HarnessProposerContext"/> record.
/// </summary>
public class HarnessProposerContextTests
{
    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var candidate = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = new HarnessSnapshot
            {
                SkillFileSnapshots = new Dictionary<string, string>(),
                SystemPromptSnapshot = "p",
                ConfigSnapshot = new Dictionary<string, string>(),
                SnapshotManifest = []
            },
            Status = HarnessCandidateStatus.Proposed
        };

        var context = new HarnessProposerContext
        {
            CurrentCandidate = candidate,
            OptimizationRunDirectoryPath = "/opt/runs/001",
            PriorCandidateIds = [Guid.NewGuid()],
            Iteration = 1
        };

        context.CurrentCandidate.Should().BeSameAs(candidate);
        context.OptimizationRunDirectoryPath.Should().Be("/opt/runs/001");
        context.PriorCandidateIds.Should().ContainSingle();
        context.Iteration.Should().Be(1);
        context.PriorLearnings.Should().BeNull();
    }
}

/// <summary>
/// Tests for <see cref="RunMetadata"/> record.
/// </summary>
public class RunMetadataTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var metadata = new RunMetadata();

        metadata.AgentName.Should().BeEmpty();
        metadata.TaskDescription.Should().BeNull();
        metadata.CandidateId.Should().BeNull();
        metadata.OptimizationRunId.Should().BeNull();
        metadata.Iteration.Should().BeNull();
        metadata.TaskId.Should().BeNull();
    }

    [Fact]
    public void Construction_WithAllProperties_SetsCorrectly()
    {
        var candidateId = Guid.NewGuid();
        var optRunId = Guid.NewGuid();
        var metadata = new RunMetadata
        {
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "test-agent",
            TaskDescription = "Evaluate math",
            CandidateId = candidateId,
            OptimizationRunId = optRunId,
            Iteration = 2,
            TaskId = "task-01"
        };

        metadata.AgentName.Should().Be("test-agent");
        metadata.CandidateId.Should().Be(candidateId);
        metadata.Iteration.Should().Be(2);
    }
}

/// <summary>
/// Tests for <see cref="TurnArtifacts"/> record.
/// </summary>
public class TurnArtifactsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var artifacts = new TurnArtifacts();

        artifacts.TurnNumber.Should().Be(0);
        artifacts.SystemPrompt.Should().BeNull();
        artifacts.ToolCallsJsonl.Should().BeNull();
        artifacts.ModelResponse.Should().BeNull();
        artifacts.StateSnapshot.Should().BeNull();
        artifacts.ToolResults.Should().BeEmpty();
    }

    [Fact]
    public void Construction_WithAllProperties_SetsCorrectly()
    {
        var toolResults = new Dictionary<string, string> { ["call-1"] = "{}" };
        var artifacts = new TurnArtifacts
        {
            TurnNumber = 3,
            SystemPrompt = "You are helpful",
            ToolCallsJsonl = "{\"tool\":\"read\"}",
            ModelResponse = "Here is the answer",
            StateSnapshot = "{}",
            ToolResults = toolResults
        };

        artifacts.TurnNumber.Should().Be(3);
        artifacts.SystemPrompt.Should().Be("You are helpful");
        artifacts.ToolResults.Should().ContainKey("call-1");
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new TurnArtifacts { TurnNumber = 1 };

        var modified = original with { TurnNumber = 2 };

        original.TurnNumber.Should().Be(1);
        modified.TurnNumber.Should().Be(2);
    }
}

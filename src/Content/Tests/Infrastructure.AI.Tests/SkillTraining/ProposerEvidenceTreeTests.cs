using Domain.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.SkillTraining;
using Xunit;

namespace Infrastructure.AI.Tests.SkillTraining;

/// <summary>
/// Pins <see cref="ProposerEvidenceTree"/> and <see cref="ProposerReply"/> (#843): the layout the proposer
/// session reads, that an item id cannot steer a file outside the tree, and how a reply is read.
/// </summary>
public sealed class ProposerEvidenceTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"proposer-tree-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static ReflectionInput Input(bool includeSuccesses = true, string memory = "", params ProposerRolloutView[] rollouts) => new()
    {
        CurrentSkill = "# skill\n- rule\n",
        Rollouts = rollouts,
        MetaSkillMemory = memory,
        IncludeSuccesses = includeSuccesses
    };

    [Fact]
    public async Task Write_LaysOutSkillRolloutsAndMemory()
    {
        var input = Input(true, "an earlier lesson",
            new ProposerRolloutView("case-1", false, "the agent gave up", string.Empty),
            new ProposerRolloutView("case-2", true, "it worked", string.Empty));

        var tree = await ProposerEvidenceTree.WriteAsync(_root, input, new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        File.ReadAllText(Path.Combine(tree.Directory, "skill", "SKILL.md")).Should().Be("# skill\n- rule\n");
        File.ReadAllText(Path.Combine(tree.Directory, "memory", "EXPERIENCE.md")).Should().Be("an earlier lesson");
        var rollouts = Directory.GetFiles(Path.Combine(tree.Directory, "rollouts")).OrderBy(f => f).ToList();
        rollouts.Should().HaveCount(2);
        File.ReadAllText(rollouts[0]).Should().Be("passed: false\nitem: case-1\n\nthe agent gave up");
        File.ReadAllText(rollouts[1]).Should().StartWith("passed: true\nitem: case-2");
        (tree.RolloutCount, tree.PassedCount, tree.HasMemory).Should().Be((2, 1, true));
    }

    [Fact]
    public async Task Write_WithoutSuccessesRequested_LeavesThePassingRolloutsOut()
    {
        var input = Input(false, string.Empty,
            new ProposerRolloutView("bad", false, "x", string.Empty),
            new ProposerRolloutView("good", true, "y", string.Empty));

        var tree = await ProposerEvidenceTree.WriteAsync(_root, input, new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        Directory.GetFiles(Path.Combine(tree.Directory, "rollouts")).Should().ContainSingle().Which.Should().Contain("bad");
        tree.HasMemory.Should().BeFalse();
    }

    [Fact]
    public async Task Write_ItemIdWithPathCharacters_CannotLeaveTheTree()
    {
        var input = Input(true, string.Empty, new ProposerRolloutView("../../../outside", false, "x", string.Empty));

        var tree = await ProposerEvidenceTree.WriteAsync(_root, input, new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        var file = Directory.GetFiles(Path.Combine(tree.Directory, "rollouts")).Single();
        Path.GetFullPath(file).Should().StartWith(Path.GetFullPath(tree.Directory));
        Path.GetFileName(file).Should().NotContain("..").And.NotContain("/").And.NotContain("\\");
    }

    [Fact]
    public async Task Delete_RemovesTheWholeTree()
    {
        var tree = await ProposerEvidenceTree.WriteAsync(
            _root, Input(true, "m", new ProposerRolloutView("a", false, "x", string.Empty)),
            new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        ProposerEvidenceTree.Delete(tree.Directory);

        Directory.Exists(tree.Directory).Should().BeFalse();
    }

    [Fact]
    public void Parse_TakesTheLastBlocksAndStripsTheTagNewlines()
    {
        var reply = ProposerReply.Parse(
            "I will use <skill>draft</skill> format.\n<notes>first</notes>\n<skill>\nline one\r\nline two\n</skill>\n<notes>\nrun 2: it timed out\n</notes>");

        reply.Skill.Should().Be("line one\nline two");
        reply.Notes.Should().Be("run 2: it timed out");
    }

    [Fact]
    public void Parse_NoSkillBlock_CarriesTheNotesAndKeepBase()
    {
        var reply = ProposerReply.Parse("<notes>KEEP_BASE: nothing in the runs argues for a change</notes>");

        reply.Skill.Should().BeNull();
        reply.SaysKeepBase.Should().BeTrue();
    }

    [Fact]
    public void Parse_Prose_HasNeitherASkillNorKeepBase()
    {
        var reply = ProposerReply.Parse("Sure, here is my answer without the format.");

        reply.Skill.Should().BeNull();
        reply.SaysKeepBase.Should().BeFalse();
    }
}

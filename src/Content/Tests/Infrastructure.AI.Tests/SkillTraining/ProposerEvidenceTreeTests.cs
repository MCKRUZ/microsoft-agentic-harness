using Domain.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.SkillTraining;
using Xunit;

namespace Infrastructure.AI.Tests.SkillTraining;

/// <summary>
/// Pins <see cref="ProposerEvidenceTree"/> (#843): the layout the proposer session reads, that an item id
/// cannot steer a file outside the tree, that a run too long to store whole is bounded, and how leaking
/// rollouts are screened out.
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
    public async Task Write_ARunTooLongToStoreWhole_KeepsItsBeginningAndEndAndSaysWhatWasOmitted()
    {
        var head = new string('h', ProposerEvidenceTree.MaxTrajectoryChars / 2);
        var tail = new string('t', ProposerEvidenceTree.MaxTrajectoryChars / 2);
        var input = Input(true, string.Empty, new ProposerRolloutView("huge", false, head + new string('m', 1000) + tail, string.Empty));

        var tree = await ProposerEvidenceTree.WriteAsync(_root, input, new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        var text = File.ReadAllText(Directory.GetFiles(Path.Combine(tree.Directory, "rollouts")).Single());
        text.Length.Should().BeLessThan(ProposerEvidenceTree.MaxTrajectoryChars + 500);
        text.Contains("1000 characters omitted from the middle").Should().BeTrue();
        text.Contains(new string('m', 100)).Should().BeFalse("the middle is what is dropped");
        text.Contains(head[..100]).Should().BeTrue();
        text.EndsWith(tail[^100..], StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public async Task Screen_ALeakingRollout_IsRemovedAndTheRestStandWithCountsToMatch()
    {
        var input = Input(true, string.Empty,
            new ProposerRolloutView("clean-pass", true, "it worked", string.Empty),
            new ProposerRolloutView("leaky", false, "{\"OverallVerdict\":\"Fail\"}", string.Empty),
            new ProposerRolloutView("clean-fail", false, "it gave up", string.Empty));
        var tree = await ProposerEvidenceTree.WriteAsync(_root, input, new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        var screened = await ProposerEvidenceTree.ScreenAsync(tree, CancellationToken.None);

        screened.Usable.Should().BeTrue("one hostile run must not stop the step for the others");
        screened.Findings.Should().ContainSingle().Which.Source.Should().Contain("leaky");
        (screened.Tree.RolloutCount, screened.Tree.PassedCount).Should().Be((2, 1));
        Directory.GetFiles(Path.Combine(tree.Directory, "rollouts")).Should().HaveCount(2)
            .And.NotContain(f => f.Contains("leaky"));
    }

    [Fact]
    public async Task Screen_EveryRolloutLeaking_IsUnusable()
    {
        var input = Input(true, string.Empty, new ProposerRolloutView("leaky", false, "{\"OverallVerdict\":\"Fail\"}", string.Empty));
        var tree = await ProposerEvidenceTree.WriteAsync(_root, input, new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        var screened = await ProposerEvidenceTree.ScreenAsync(tree, CancellationToken.None);

        screened.Usable.Should().BeFalse();
    }

    [Fact]
    public async Task Screen_NoRolloutsToBeginWith_IsStillUsable()
    {
        var tree = await ProposerEvidenceTree.WriteAsync(_root, Input(), new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        var screened = await ProposerEvidenceTree.ScreenAsync(tree, CancellationToken.None);

        screened.Usable.Should().BeTrue("nothing leaked and there was nothing to leave out");
        screened.Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("memory")]
    public async Task Screen_AMarkerInTheSkillOrTheMemory_IsUnusable(string where)
    {
        var verdict = "{\"OverallVerdict\":\"Fail\"}";
        var input = new ReflectionInput
        {
            CurrentSkill = where == "skill" ? verdict : "# skill\n",
            MetaSkillMemory = where == "memory" ? verdict : string.Empty,
            Rollouts = [new ProposerRolloutView("clean", false, "it gave up", string.Empty)],
            IncludeSuccesses = true
        };
        var tree = await ProposerEvidenceTree.WriteAsync(_root, input, new OwnerOnlyDirectoryCreator(), CancellationToken.None);

        var screened = await ProposerEvidenceTree.ScreenAsync(tree, CancellationToken.None);

        screened.Usable.Should().BeFalse("there is no skill or memory to leave out and still have a step");
        screened.Findings.Should().ContainSingle();
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
}

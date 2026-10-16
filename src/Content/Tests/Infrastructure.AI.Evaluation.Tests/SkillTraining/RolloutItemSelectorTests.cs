using Domain.AI.Evaluation;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Evaluation.SkillTraining;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.SkillTraining;

/// <summary>
/// Pins <see cref="RolloutItemSelector"/> (#843): a case is in a split only by an explicit tag, so the
/// validation set is never silently the training set; a pinned batch is exactly what was asked, and a
/// sampled one depends on the seed and not on the order the file happens to be in.
/// </summary>
public sealed class RolloutItemSelectorTests
{
    private static EvalCase Case(string id, params string[] tags) => new()
    {
        Id = id,
        Input = "task " + id,
        Tags = tags,
        MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }]
    };

    private static EvalDataset Dataset(params EvalCase[] cases) => new() { Name = "d", Cases = cases };

    private static EvalDataset Split(int train, int val) => Dataset(
        [.. Enumerable.Range(0, train).Select(i => Case($"t{i:D2}", "split:train", "arithmetic")),
         .. Enumerable.Range(0, val).Select(i => Case($"v{i:D2}", "split:val"))]);

    [Fact]
    public void Select_ReturnsOnlyTheRequestedSplit()
    {
        var chosen = RolloutItemSelector.Select(Split(5, 3), new RolloutBatch { Split = "val", BatchSize = 100 });

        chosen.Select(c => c.Id).Should().BeEquivalentTo("v00", "v01", "v02");
    }

    [Fact]
    public void Select_ACaseWithNoSplitTag_IsNeverRolledOut()
    {
        var dataset = Dataset(Case("a", "split:train"), Case("untagged", "rag"));

        var chosen = RolloutItemSelector.Select(dataset, new RolloutBatch { Split = "train", BatchSize = 10 });

        chosen.Select(c => c.Id).Should().Equal("a");
    }

    [Fact]
    public void Select_ADatasetWithNoSplitTagsAtAll_IsRefusedNotTreatedAsOneSplit()
    {
        var act = () => RolloutItemSelector.Select(Dataset(Case("a", "rag"), Case("b")), new RolloutBatch { Split = "val" });

        act.Should().Throw<InvalidOperationException>().WithMessage("skill_training.rollout_dataset_unsplit*");
    }

    [Fact]
    public void Select_PinnedIds_AreReturnedInTheOrderAskedFor()
    {
        var batch = new RolloutBatch { Split = "val", ItemIds = ["v02", "v00"], BatchSize = 1 };

        RolloutItemSelector.Select(Split(2, 3), batch).Select(c => c.Id).Should().Equal("v02", "v00");
    }

    [Fact]
    public void Select_APinnedIdFromAnotherSplit_IsRefused()
    {
        var batch = new RolloutBatch { Split = "val", ItemIds = ["t00"] };

        var act = () => RolloutItemSelector.Select(Split(2, 2), batch);

        act.Should().Throw<InvalidOperationException>().WithMessage("skill_training.rollout_item_missing*");
    }

    [Fact]
    public void Select_ASample_IsCappedAtTheBatchSize()
    {
        var chosen = RolloutItemSelector.Select(Split(20, 0), new RolloutBatch { Split = "train", BatchSize = 6, Seed = 3 });

        chosen.Should().HaveCount(6).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Select_ASample_DependsOnTheSeedNotOnTheOrderOfTheFile()
    {
        var cases = Enumerable.Range(0, 20).Select(i => Case($"t{i:D2}", "split:train")).ToArray();
        var batch = new RolloutBatch { Split = "train", BatchSize = 5, Seed = 11 };

        var inOrder = RolloutItemSelector.Select(Dataset(cases), batch).Select(c => c.Id);
        var reversed = RolloutItemSelector.Select(Dataset([.. cases.Reverse()]), batch).Select(c => c.Id);
        var otherSeed = RolloutItemSelector.Select(Dataset(cases), batch with { Seed = 12 }).Select(c => c.Id);

        reversed.Should().Equal(inOrder, "the same seed picks the same items however the file is ordered");
        otherSeed.Should().NotEqual(inOrder, "a different seed is a different sample");
    }

    [Fact]
    public void Select_TheSameCaseIdTwiceInASplit_IsRefusedBecauseItemsCouldNotBePaired()
    {
        var dataset = Dataset(Case("dup", "split:val"), Case("dup", "split:val"));

        var act = () => RolloutItemSelector.Select(dataset, new RolloutBatch { Split = "val" });

        act.Should().Throw<InvalidOperationException>().WithMessage("skill_training.rollout_duplicate_item*");
    }

    [Fact]
    public void Select_AnEmptySplit_IsRefused()
    {
        var act = () => RolloutItemSelector.Select(Split(3, 0), new RolloutBatch { Split = "val" });

        act.Should().Throw<InvalidOperationException>().WithMessage("skill_training.rollout_split_empty*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Select_ABatchWithNoSplit_IsRefused(string split)
    {
        var act = () => RolloutItemSelector.Select(Split(1, 1), new RolloutBatch { Split = split });

        act.Should().Throw<InvalidOperationException>().WithMessage("skill_training.rollout_split_missing*");
    }

    [Fact]
    public void Select_SplitMatchingIgnoresCase()
    {
        var chosen = RolloutItemSelector.Select(Dataset(Case("a", "Split:Val")), new RolloutBatch { Split = "val" });

        chosen.Should().ContainSingle();
    }
}

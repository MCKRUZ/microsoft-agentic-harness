using Domain.AI.Retrieval;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Retrieval;

/// <summary>
/// Tests for the generic <see cref="ReciprocalRankFusion"/> primitive: cross-list fusion, deterministic
/// ordering, per-list contribution tracking, the topK cap, and argument validation.
/// </summary>
public sealed class ReciprocalRankFusionTests
{
    private sealed record Item(string Key, string Payload);

    private static IReadOnlyList<Item> List(params string[] keys) =>
        keys.Select(k => new Item(k, k)).ToList();

    [Fact]
    public void SingleList_PreservesOrder_ScoresDecreaseByRank()
    {
        var fused = ReciprocalRankFusion.Fuse([List("a", "b", "c")], static i => i.Key);

        fused.Select(f => f.Item.Key).Should().Equal("a", "b", "c");
        fused.Select(f => f.Score).Should().BeInDescendingOrder();
    }

    [Fact]
    public void SharedItem_SumsContributions_AndRanksAboveSingletons()
    {
        // "shared" appears at rank 0 in both lists, so its summed RRF score (two rank-0 contributions) beats
        // any item that appears in only one list.
        var listA = List("shared", "x", "y");
        var listB = List("shared", "p", "q");

        var fused = ReciprocalRankFusion.Fuse([listA, listB], static i => i.Key, k: 1.0);

        fused[0].Item.Key.Should().Be("shared", "an item found by both lists outranks any single-list hit");
        fused[0].Score.Should().BeApproximately((1.0 / (1 + 0 + 1)) * 2, 1e-9);
    }

    [Fact]
    public void PerListItems_TracksContributionPositionally()
    {
        var dense = new[] { new Item("shared", "from-dense"), new Item("d-only", "d") };
        var sparse = new[] { new Item("s-only", "s"), new Item("shared", "from-sparse") };

        var fused = ReciprocalRankFusion.Fuse([dense, sparse], static i => i.Key);

        var shared = fused.Single(f => f.Item.Key == "shared");
        shared.PerListItems.Should().HaveCount(2);
        shared.PerListItems[0]!.Payload.Should().Be("from-dense", "list 0's contribution is kept at index 0");
        shared.PerListItems[1]!.Payload.Should().Be("from-sparse", "list 1's contribution is kept at index 1");

        var dOnly = fused.Single(f => f.Item.Key == "d-only");
        dOnly.PerListItems[0].Should().NotBeNull();
        dOnly.PerListItems[1].Should().BeNull("d-only never appeared in list 1");
    }

    [Fact]
    public void Representative_IsFromEarliestList()
    {
        var listA = new[] { new Item("shared", "A") };
        var listB = new[] { new Item("shared", "B") };

        var fused = ReciprocalRankFusion.Fuse([listA, listB], static i => i.Key);

        fused.Single().Item.Payload.Should().Be("A", "the representative is the earliest list's item");
    }

    [Fact]
    public void EqualScores_BreakTiesByFirstAppearance_Deterministic()
    {
        // Two disjoint items both at rank 0 of their own list have identical scores; the one seen first
        // (list 0) must sort first, every time.
        var fused = ReciprocalRankFusion.Fuse([List("first"), List("second")], static i => i.Key);

        fused.Select(f => f.Item.Key).Should().Equal("first", "second");
        fused[0].Score.Should().BeApproximately(fused[1].Score, 1e-9, "the two singletons tie on score");
    }

    [Fact]
    public void DuplicateKeyWithinList_CountedOnceAtBestRank()
    {
        // "dup" appears twice in the same list (rank 0 and rank 2). Its contribution must be counted once,
        // at its best (rank 0) score — summing both occurrences would inflate its fused score.
        var list = new[] { new Item("dup", "first"), new Item("x", "x"), new Item("dup", "second") };

        var fused = ReciprocalRankFusion.Fuse([list], static i => i.Key, k: 1.0);

        fused.Should().HaveCount(2, "the duplicate key collapses to a single fused entry");
        var dup = fused.Single(f => f.Item.Key == "dup");
        dup.Score.Should().BeApproximately(1.0 / (1 + 0 + 1), 1e-9, "counted once at its best rank, not summed");
        dup.Item.Payload.Should().Be("first", "the representative is the best-rank (first-seen) occurrence");
    }

    [Fact]
    public void TopK_CapsResults()
    {
        var fused = ReciprocalRankFusion.Fuse([List("a", "b", "c", "d")], static i => i.Key, topK: 2);

        fused.Should().HaveCount(2);
        fused.Select(f => f.Item.Key).Should().Equal("a", "b");
    }

    [Fact]
    public void EmptyLists_ReturnEmpty()
    {
        var fused = ReciprocalRankFusion.Fuse<Item>([[], []], static i => i.Key);

        fused.Should().BeEmpty();
    }

    [Fact]
    public void NonPositiveK_Throws()
    {
        var act = () => ReciprocalRankFusion.Fuse([List("a")], static i => i.Key, k: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NegativeTopK_Throws()
    {
        var act = () => ReciprocalRankFusion.Fuse([List("a")], static i => i.Key, topK: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var nullLists = () => ReciprocalRankFusion.Fuse(null!, static (Item i) => i.Key);
        var nullSelector = () => ReciprocalRankFusion.Fuse([List("a")], null!);

        nullLists.Should().Throw<ArgumentNullException>();
        nullSelector.Should().Throw<ArgumentNullException>();
    }
}

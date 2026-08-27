using Application.AI.Common.Services;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Tests for <see cref="ReplayedToolCallSet"/> (#509).
/// </summary>
/// <remarks>
/// The invariant that matters is <em>exactly-once claiming under concurrency</em>, not merely that the
/// underlying collection survives being touched by two threads. The set exists so
/// <c>ToolDiagnosticsMiddleware</c> records each tool result once; if two concurrent callers could both
/// be told they were first, the middleware would write the duplicate trace record the set exists to
/// prevent — which a lock around a separate <c>Contains</c> and <c>Add</c> would not have stopped.
/// </remarks>
public sealed class ReplayedToolCallSetTests
{
    [Fact]
    public void TryClaim_UnknownId_ReturnsTrueAndBecomesKnown()
    {
        var set = new ReplayedToolCallSet();

        set.TryClaim("call-1").Should().BeTrue();
        set.Contains("call-1").Should().BeTrue();
        set.Count.Should().Be(1);
    }

    [Fact]
    public void TryClaim_SameIdTwice_SecondCallerIsRefused()
    {
        var set = new ReplayedToolCallSet();

        set.TryClaim("call-1").Should().BeTrue();
        set.TryClaim("call-1").Should().BeFalse();
        set.Count.Should().Be(1);
    }

    [Fact]
    public void TryClaim_IdSeededFromReplayedHistory_IsRefused()
    {
        var set = new ReplayedToolCallSet(["call-1", "call-2"]);

        set.TryClaim("call-1").Should().BeFalse();
        set.TryClaim("call-3").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryClaim_EmptyId_IsNeverClaimable(string? callId)
    {
        var set = new ReplayedToolCallSet();

        set.TryClaim(callId).Should().BeFalse(
            "a result with no id cannot be deduplicated, so the caller — not this type — decides what " +
            "to do with it");
        set.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_SeedWithEmptyAndDuplicateEntries_AbsorbsThem()
    {
        var set = new ReplayedToolCallSet(["call-1", "call-1", "", "call-2"]);

        set.Count.Should().Be(2,
            "the seed comes from persisted conversation rows, whose shape the seeding code does not " +
            "control");
    }

    [Fact]
    public void Contains_IsOrdinal_SoCasingDistinguishesIds()
    {
        var set = new ReplayedToolCallSet(["Call-1"]);

        set.Contains("call-1").Should().BeFalse(
            "a call id is an opaque provider token; case-insensitive matching would collapse two " +
            "genuinely distinct ids");
        set.TryClaim("call-1").Should().BeTrue();
    }

    [Fact]
    public void TryClaim_ManyThreadsRacingOnOneId_ExactlyOneWins()
    {
        // The defect this closes: two tools driving the same middleware pipeline concurrently share one
        // set by reference (the chat client is built with AllowConcurrentInvocation = true). A separate
        // Contains-then-Add — even with each half individually locked — lets both read "absent," both
        // add, and both record the same tool result.
        //
        // A Barrier repeated over many rounds, rather than one thread-start stampede: the window a
        // check-then-act leaves open is nanoseconds wide, and starting N threads once almost never
        // lands inside it — a version of this test built that way passed against a deliberately
        // check-then-act implementation three runs in a row, proving nothing. The barrier releases
        // every thread within the same scheduling quantum, and doing that thousands of times turns a
        // rare interleaving into a near-certain one.
        const int racers = 8;
        const int rounds = 5000;

        var set = new ReplayedToolCallSet();
        var ids = Enumerable.Range(0, rounds).Select(r => $"contended-{r}").ToArray();
        var winsPerThread = new int[racers];
        using var barrier = new Barrier(racers);

        var threads = Enumerable.Range(0, racers)
            .Select(index => new Thread(() =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    barrier.SignalAndWait();
                    if (set.TryClaim(ids[round]))
                        winsPerThread[index]++;
                }
            }))
            .ToList();

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            thread.Join();

        winsPerThread.Sum().Should().Be(rounds,
            "exactly one caller per id may be told it is the first to claim it — no more, no fewer");
        set.Count.Should().Be(rounds);
    }

    [Fact]
    public void TryClaim_ManyThreadsClaimingDistinctIds_AllSucceedAndNoneAreLost()
    {
        // The other half of the hazard: HashSet<T> is not thread-safe, so concurrent Add calls can lose
        // an update or corrupt a bucket chain outright. Distinct ids means every claim must succeed.
        const int racers = 64;
        var set = new ReplayedToolCallSet();
        var winners = 0;
        using var startGate = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, racers)
            .Select(i => new Thread(() =>
            {
                startGate.Wait();
                if (set.TryClaim($"call-{i}"))
                    Interlocked.Increment(ref winners);
            }))
            .ToList();

        foreach (var thread in threads)
            thread.Start();

        startGate.Set();

        foreach (var thread in threads)
            thread.Join();

        winners.Should().Be(racers);
        set.Count.Should().Be(racers);
    }

    // ===== Bounded construction (#505) =====
    //
    // ToolDiagnosticsMiddleware's per-instance fallback can be process-lived (Presentation.FoundryHost
    // builds one agent, and one middleware instance, for the whole container) — the fresh local
    // correctness gate caught this default-constructor set growing forever and permanently refusing
    // to re-record any repeated call id. These tests are on the type the fix landed in, not the
    // middleware, because the invariant ("never exceeds the bound, oldest claim falls out first") is
    // this type's to keep regardless of who constructs it that way.

    [Fact]
    public void BoundedConstructor_ClaimingBeyondTheBound_EvictsTheOldestClaim()
    {
        var set = new ReplayedToolCallSet([], maxEntries: 2);

        set.TryClaim("call-1").Should().BeTrue();
        set.TryClaim("call-2").Should().BeTrue();
        set.TryClaim("call-3").Should().BeTrue();

        set.Count.Should().Be(2, "the bound must never be exceeded, however many ids are claimed");
        set.Contains("call-1").Should().BeFalse("the oldest claim is the one that falls out first");
        set.Contains("call-2").Should().BeTrue();
        set.Contains("call-3").Should().BeTrue();
    }

    [Fact]
    public void BoundedConstructor_AnEvictedId_CanBeLegitimatelyReclaimed()
    {
        // The whole point of bounding rather than leaving the set unbounded: a very old id falling
        // out of the window is not an error state, it is what makes the type usable at all behind a
        // process-lived caller. Before this fix, a process-lived set's answer to "is this known" was
        // permanent once true; after, it is "known within the retained window."
        var set = new ReplayedToolCallSet([], maxEntries: 1);

        set.TryClaim("call-1").Should().BeTrue();
        set.TryClaim("call-2").Should().BeTrue(); // evicts call-1

        set.TryClaim("call-1").Should().BeTrue(
            "an id that fell out of the bounded window is, correctly, claimable again — TryClaim's " +
            "contract is 'known right now', never 'was ever claimed'");
    }

    [Fact]
    public void BoundedConstructor_SeedLargerThanTheBound_IsTrimmedBeforeAnyClaim()
    {
        // A long replayed history can already exceed the bound on its own. Without trimming at
        // construction, an oversized seed would sit above the bound indefinitely if the caller never
        // claims anything new — ClaimCore's eviction only runs on an ADD.
        var set = new ReplayedToolCallSet(["call-1", "call-2", "call-3"], maxEntries: 2);

        set.Count.Should().Be(2);
        set.Contains("call-1").Should().BeFalse("the earliest seed entries are trimmed first, FIFO");
        set.Contains("call-3").Should().BeTrue();
    }

    [Fact]
    public void BoundedConstructor_SeedWithDuplicatesAtTheBound_CountsEachIdOnce()
    {
        // The seed-trimming loop counts _callIds (deduplicated), not the raw seed enumerable. A seed
        // with duplicates could otherwise be trimmed too aggressively or not enough depending on
        // which count the eviction loop actually reads.
        var set = new ReplayedToolCallSet(["call-1", "call-1", "call-2"], maxEntries: 2);

        set.Count.Should().Be(2);
        set.Contains("call-1").Should().BeTrue();
        set.Contains("call-2").Should().BeTrue();
    }

    [Fact]
    public void UnboundedConstructor_ClaimingManyIds_NeverEvicts()
    {
        // Control for the two constructors above: the turn-scoped seed usage this type was built for
        // must stay genuinely unbounded — a turn's own lifetime already bounds it, and evicting there
        // would silently reintroduce the intra-turn duplicate-recording bug ReplayedToolCallScope
        // exists to prevent.
        var set = new ReplayedToolCallSet();

        for (var i = 0; i < 5_000; i++)
            set.TryClaim($"call-{i}").Should().BeTrue();

        set.Count.Should().Be(5_000);
        set.Contains("call-0").Should().BeTrue("nothing evicts when no bound was requested");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BoundedConstructor_NonPositiveMaxEntries_Throws(int maxEntries)
    {
        var act = () => new ReplayedToolCallSet([], maxEntries);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

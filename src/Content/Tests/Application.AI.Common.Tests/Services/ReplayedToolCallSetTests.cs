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
}

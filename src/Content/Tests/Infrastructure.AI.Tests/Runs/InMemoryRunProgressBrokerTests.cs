using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="InMemoryRunProgressBroker"/>.
/// </summary>
/// <remarks>
/// The properties that carry consequences: publishing must never block the run (a watcher is an
/// observer, and letting a slow reader hold up paid work would be the wrong trade), a watcher that
/// falls behind must be told so rather than silently shown an incomplete run, and two watchers of one
/// run must not cost each other events.
/// </remarks>
public sealed class InMemoryRunProgressBrokerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();

    private InMemoryRunProgressBroker BuildSut(int buffer = 256, int maxStreams = 64, int perOwner = 64)
    {
        _config.AI.WorkflowSubmission.ProgressBufferSize = buffer;
        _config.AI.WorkflowSubmission.MaxConcurrentProgressStreams = maxStreams;
        _config.AI.WorkflowSubmission.MaxProgressStreamsPerOwner = perOwner;
        return new InMemoryRunProgressBroker(new StaticOptionsMonitor<AppConfig>(_config), _time);
    }

    private static async Task<List<RunProgressEvent>> DrainAsync(
        IRunProgressSubscription subscription, int expected)
    {
        var received = new List<RunProgressEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var evt in subscription.ReadAllAsync(cts.Token))
        {
            received.Add(evt);
            if (received.Count == expected)
                break;
        }

        return received;
    }

    [Fact]
    public async Task ASubscriberReceivesWhatIsPublishedForItsRun()
    {
        var sut = BuildSut();
        using var subscription = sut.Subscribe("job-1", "alice", "acme")!;

        sut.Publish("job-1", RunProgressKind.StepStarted, stepId: "s1", stepName: "first");
        sut.Publish("job-1", RunProgressKind.StepCompleted, stepId: "s1", status: "Completed");

        var received = await DrainAsync(subscription, 2);

        received.Should().HaveCount(2);
        received[0].Kind.Should().Be(RunProgressKind.StepStarted);
        received[0].StepName.Should().Be("first");
        received[1].Kind.Should().Be(RunProgressKind.StepCompleted);
    }

    [Fact]
    public async Task EventsForOtherRunsAreNotDelivered()
    {
        // A job identifier is the only thing separating callers here too. A watcher that received
        // another run's steps would learn about work it was never given the identifier for.
        var sut = BuildSut();
        using var subscription = sut.Subscribe("job-1", "alice", "acme")!;

        sut.Publish("job-2", RunProgressKind.StepStarted, stepId: "not-mine");
        sut.Publish("job-1", RunProgressKind.StepStarted, stepId: "mine");

        var received = await DrainAsync(subscription, 1);

        received.Should().ContainSingle();
        received[0].StepId.Should().Be("mine");
    }

    [Fact]
    public void PublishingWithNoSubscribers_IsANoOpRatherThanABuffer()
    {
        // Holding a transcript for a watcher who may never arrive means deciding how long to keep it.
        // A late watcher is given the run's current state instead, which is bounded and honest.
        var sut = BuildSut(buffer: 2);

        for (var i = 0; i < 1000; i++)
            sut.Publish("job-1", RunProgressKind.StepStarted, stepId: $"s{i}");

        using var subscription = sut.Subscribe("job-1", "alice", "acme")!;
        sut.Publish("job-1", RunProgressKind.StepCompleted, stepId: "after");

        subscription.DroppedCount.Should().Be(0, "nothing was buffered, so nothing was dropped");
    }

    [Fact]
    public async Task Sequence_StartsAtOneAndIncreasesPerRun()
    {
        // The numbering is what makes a gap detectable. Without it a client cannot tell a stream that
        // skipped an event from one that had nothing to say.
        var sut = BuildSut();
        using var first = sut.Subscribe("job-1", "alice", "acme")!;
        using var second = sut.Subscribe("job-2", "alice", "acme")!;

        sut.Publish("job-1", RunProgressKind.StepStarted);
        sut.Publish("job-1", RunProgressKind.StepCompleted);
        sut.Publish("job-2", RunProgressKind.StepStarted);

        var firstEvents = await DrainAsync(first, 2);
        var secondEvents = await DrainAsync(second, 1);

        firstEvents.Select(e => e.Sequence).Should().Equal(1, 2);
        secondEvents.Select(e => e.Sequence).Should().Equal([1L],
            "each run is numbered independently, so a second run does not inherit the first's position");
    }

    [Fact]
    public void ASlowWatcherLosesEventsAndIsToldSo()
    {
        // The alternative on a full buffer is to block the publisher, which is the run — an observer
        // would then be able to slow paid work down by reading slowly, or by walking away.
        var sut = BuildSut(buffer: 4);
        using var subscription = sut.Subscribe("job-1", "alice", "acme")!;

        for (var i = 0; i < 20; i++)
            sut.Publish("job-1", RunProgressKind.StepStarted, stepId: $"s{i}");

        subscription.DroppedCount.Should().BeGreaterThan(0,
            "a watcher that cannot keep up must be able to tell that its view has gaps");
    }

    [Fact]
    public async Task ASlowWatcherDoesNotCostAnotherWatcherEvents()
    {
        // Per-watcher buffers, not per run. A shared buffer would make the slowest reader the whole
        // run's reader.
        var sut = BuildSut(buffer: 4);
        using var slow = sut.Subscribe("job-1", "alice", "acme")!;
        using var fast = sut.Subscribe("job-1", "alice", "acme")!;

        for (var i = 0; i < 4; i++)
            sut.Publish("job-1", RunProgressKind.StepStarted, stepId: $"s{i}");

        var received = await DrainAsync(fast, 4);

        received.Should().HaveCount(4);
        fast.DroppedCount.Should().Be(0);
    }

    [Fact]
    public void Subscribe_BeyondTheHostsStreamLimit_IsRefused()
    {
        // Each open stream holds a connection and a buffer for as long as the client keeps it, so an
        // unbounded number of them exhausts the host by asking politely.
        var sut = BuildSut(maxStreams: 2);

        using var first = sut.Subscribe("job-1", "alice", "acme");
        using var second = sut.Subscribe("job-2", "alice", "acme");
        var third = sut.Subscribe("job-3", "alice", "acme");

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        third.Should().BeNull();
    }

    [Fact]
    public void DisposingASubscription_ReturnsItsSlotToTheHost()
    {
        // Without this a host that has served its limit once can never serve another stream, and the
        // refusal looks identical to genuine saturation.
        var sut = BuildSut(maxStreams: 1);

        var first = sut.Subscribe("job-1", "alice", "acme");
        first.Should().NotBeNull();
        sut.Subscribe("job-2", "alice", "acme").Should().BeNull();

        first!.Dispose();

        using var afterRelease = sut.Subscribe("job-2", "alice", "acme");
        afterRelease.Should().NotBeNull();
    }

    [Fact]
    public void OneCallerCannotOccupyEveryStreamSlot()
    {
        // A host-wide ceiling on its own is a ceiling any single caller can take: it opens streams to
        // its own runs and holds the connections, and every other tenant is refused for as long as it
        // does. Rate limiting does not help — it bounds how often a caller asks, not how long it holds.
        var sut = BuildSut(maxStreams: 64, perOwner: 2);

        using var mineOne = sut.Subscribe("job-1", "alice", "acme");
        using var mineTwo = sut.Subscribe("job-2", "alice", "acme");
        var mineThree = sut.Subscribe("job-3", "alice", "acme");

        mineThree.Should().BeNull("a caller is bounded by its own ceiling, not only the host's");

        using var theirs = sut.Subscribe("job-4", "bob", "acme");
        theirs.Should().NotBeNull("one caller at its limit must not deny the endpoint to everyone else");
    }

    [Fact]
    public void TheSameOwnerInAnotherTenantIsADifferentPrincipal()
    {
        // The same two legs that decide whether a caller may read the run at all.
        var sut = BuildSut(perOwner: 1);

        using var acme = sut.Subscribe("job-1", "alice", "acme");
        using var other = sut.Subscribe("job-2", "alice", "other-tenant");

        acme.Should().NotBeNull();
        other.Should().NotBeNull();
    }

    [Fact]
    public void ARefusedSubscriptionDoesNotConsumeASlot()
    {
        // A refusal that still charged the caller would ratchet: repeated attempts would exhaust the
        // very allowance being enforced, and the caller could never recover without a restart.
        var sut = BuildSut(perOwner: 1);

        using var held = sut.Subscribe("job-1", "alice", "acme");
        for (var attempt = 0; attempt < 5; attempt++)
            sut.Subscribe("job-2", "alice", "acme").Should().BeNull();

        held!.Dispose();

        using var afterRelease = sut.Subscribe("job-2", "alice", "acme");
        afterRelease.Should().NotBeNull();
    }

    [Fact]
    public void DisposingTwice_DoesNotHandBackASlotTwice()
    {
        // Double disposal is ordinary — a using block around something already disposed. Refunding
        // twice would inflate the allowance until the caps stopped meaning anything.
        var sut = BuildSut(perOwner: 1);

        var subscription = sut.Subscribe("job-1", "alice", "acme")!;
        subscription.Dispose();
        subscription.Dispose();

        using var first = sut.Subscribe("job-2", "alice", "acme");
        var second = sut.Subscribe("job-3", "alice", "acme");

        first.Should().NotBeNull();
        second.Should().BeNull("the caller holds one stream, so its second must still be refused");
    }

    [Fact]
    public async Task AWatcherArrivingAsAnotherLeaves_StillReceivesEvents()
    {
        // Removing a run's watcher set after observing it empty is a check-then-act: a watcher that
        // subscribes in between is added to a set that is then unregistered, and receives nothing for
        // the rest of the run while believing it saw everything.
        var sut = BuildSut();

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var leaving = sut.Subscribe("job-1", "alice", "acme")!;
            var arriving = sut.Subscribe("job-1", "bob", "acme")!;

            leaving.Dispose();
            sut.Publish("job-1", RunProgressKind.StepStarted, stepId: $"s{attempt}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = 0;
            await foreach (var _ in arriving.ReadAllAsync(cts.Token))
            {
                received++;
                break;
            }

            received.Should().Be(1, "a watcher that subscribed must receive what is published after it");
            arriving.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentPublishers_DeliverInSequenceOrder()
    {
        // A plan runs steps concurrently, so two threads publish at once. Taking a number and then
        // writing as separate steps lets the thread that drew 4 write after the thread that drew 5 —
        // and a client reading Sequence as the run's order, which is what it is documented as, would
        // report a gap that never happened.
        var sut = BuildSut(buffer: 512);
        using var subscription = sut.Subscribe("job-1", "alice", "acme")!;

        const int Publishers = 16;
        const int Each = 20;

        var threads = Enumerable.Range(0, Publishers).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < Each; i++)
                sut.Publish("job-1", RunProgressKind.StepStarted, stepId: "s");
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        var received = await DrainAsync(subscription, Publishers * Each);
        var sequences = received.Select(e => e.Sequence).ToList();

        sequences.Should().BeInAscendingOrder("delivery order must match the numbering a client reads");
        sequences.Should().OnlyHaveUniqueItems("a number identifies one event, so it is never reused");
    }

    [Fact]
    public void ForgettingARunSomeoneIsStillWatching_CompletesWhenTheyLeave()
    {
        // Forget is called once per run, by the sweep. Returning early because a watcher was attached
        // and recording nothing would hold that run's entries for the life of the process — nothing
        // calls Forget again. Safe to finish on the watcher's way out precisely because the run's
        // records are already gone, so nothing can subscribe to or publish for it again.
        var sut = BuildSut();
        var watcher = sut.Subscribe("job-1", "alice", "acme")!;

        sut.Forget("job-1");
        sut.HeldRunCount.Should().Be(1, "the run is still being watched, so nothing is reclaimed yet");

        watcher.Dispose();

        sut.HeldRunCount.Should().Be(0, "the last watcher out finishes what the sweep deferred");
    }

    [Fact]
    public void ForgettingAnUnwatchedRun_ReclaimsItImmediately()
    {
        var sut = BuildSut();
        sut.Subscribe("job-1", "alice", "acme")!.Dispose();
        sut.Publish("job-1", RunProgressKind.StepStarted);

        sut.Forget("job-1");

        sut.HeldRunCount.Should().Be(0);
    }

    [Fact]
    public void ARunNobodyForgot_IsStillHeld()
    {
        // The negative case, so the two tests above cannot both pass by reclaiming everything.
        var sut = BuildSut();
        sut.Subscribe("job-1", "alice", "acme")!.Dispose();
        sut.Publish("job-1", RunProgressKind.StepStarted);

        sut.HeldRunCount.Should().Be(1, "reclaiming is the sweep's decision, not a side effect of leaving");
    }

    [Fact]
    public void PublishingAfterTheLastWatcherLeaves_IsHarmless()
    {
        // Runs outlive their watchers routinely — a client closes the tab and the workflow carries on.
        var sut = BuildSut();
        var subscription = sut.Subscribe("job-1", "alice", "acme")!;
        subscription.Dispose();

        var act = () => sut.Publish("job-1", RunProgressKind.RunFinished, status: "Succeeded");

        act.Should().NotThrow();
    }
}

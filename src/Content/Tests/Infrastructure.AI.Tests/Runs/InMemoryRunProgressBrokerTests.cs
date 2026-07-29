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

    private InMemoryRunProgressBroker BuildSut(int buffer = 256, int maxStreams = 64)
    {
        _config.AI.WorkflowSubmission.ProgressBufferSize = buffer;
        _config.AI.WorkflowSubmission.MaxConcurrentProgressStreams = maxStreams;
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
        using var subscription = sut.Subscribe("job-1")!;

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
        using var subscription = sut.Subscribe("job-1")!;

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

        using var subscription = sut.Subscribe("job-1")!;
        sut.Publish("job-1", RunProgressKind.StepCompleted, stepId: "after");

        subscription.DroppedCount.Should().Be(0, "nothing was buffered, so nothing was dropped");
    }

    [Fact]
    public async Task Sequence_StartsAtOneAndIncreasesPerRun()
    {
        // The numbering is what makes a gap detectable. Without it a client cannot tell a stream that
        // skipped an event from one that had nothing to say.
        var sut = BuildSut();
        using var first = sut.Subscribe("job-1")!;
        using var second = sut.Subscribe("job-2")!;

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
        using var subscription = sut.Subscribe("job-1")!;

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
        using var slow = sut.Subscribe("job-1")!;
        using var fast = sut.Subscribe("job-1")!;

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

        using var first = sut.Subscribe("job-1");
        using var second = sut.Subscribe("job-2");
        var third = sut.Subscribe("job-3");

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

        var first = sut.Subscribe("job-1");
        first.Should().NotBeNull();
        sut.Subscribe("job-2").Should().BeNull();

        first!.Dispose();

        using var afterRelease = sut.Subscribe("job-2");
        afterRelease.Should().NotBeNull();
    }

    [Fact]
    public void PublishingAfterTheLastWatcherLeaves_IsHarmless()
    {
        // Runs outlive their watchers routinely — a client closes the tab and the workflow carries on.
        var sut = BuildSut();
        var subscription = sut.Subscribe("job-1")!;
        subscription.Dispose();

        var act = () => sut.Publish("job-1", RunProgressKind.RunFinished, status: "Succeeded");

        act.Should().NotThrow();
    }
}

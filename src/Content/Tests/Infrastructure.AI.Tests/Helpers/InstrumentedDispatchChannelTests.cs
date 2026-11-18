using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Runs;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// <see cref="InstrumentedDispatchChannel"/> and the three dispatch queues built on it.
/// </summary>
/// <remarks>
/// These measurements are what tells an operator that accepted work is piling up behind the dispatcher. A
/// request can answer 202 in 20 ms while its run waits a minute for a free slot, and no request metric
/// shows that. A depth that drifts or a wait that is never recorded would hide exactly that backlog.
/// </remarks>
public sealed class InstrumentedDispatchChannelTests
{
    private const string TestQueue = "test_queue";

    [Fact]
    public async Task EnqueueThenDequeue_CountsTheItemUpWhileWaitingAndDownWhenTaken()
    {
        using var recorded = new FlowScopedMeasurements(DispatchConventions.QueueDepth);
        var channel = new InstrumentedDispatchChannel(TestQueue);

        await channel.EnqueueAsync("a", CancellationToken.None);
        await channel.EnqueueAsync("b", CancellationToken.None);
        recorded.Values(DispatchConventions.QueueDepth, DispatchConventions.Queue, TestQueue).Sum()
            .Should().Be(2, "two items are waiting");

        await TakeAsync(channel, 1);

        recorded.Values(DispatchConventions.QueueDepth, DispatchConventions.Queue, TestQueue).Sum()
            .Should().Be(1, "one item was taken and one is still waiting");
    }

    [Fact]
    public async Task Dequeue_RecordsHowLongTheItemWaited()
    {
        using var recorded = new FlowScopedMeasurements(DispatchConventions.QueueWait);
        var channel = new InstrumentedDispatchChannel(TestQueue);

        await channel.EnqueueAsync("a", CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(80));
        await TakeAsync(channel, 1);

        recorded.Values(DispatchConventions.QueueWait, DispatchConventions.Queue, TestQueue)
            .Should().ContainSingle().Which.Should().BeGreaterThanOrEqualTo(70, "the item sat in the queue for 80 ms");
    }

    [Fact]
    public async Task Dequeue_YieldsItemsInTheOrderTheyWereEnqueued()
    {
        var channel = new InstrumentedDispatchChannel(TestQueue);
        foreach (var id in new[] { "first", "second", "third" })
            await channel.EnqueueAsync(id, CancellationToken.None);

        (await TakeAsync(channel, 3)).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task CancelledEnqueue_LeavesTheDepthAsItWas()
    {
        // The depth goes up before the write so the dispatcher can never count an item down first. A write
        // that then fails must give it back, or every cancelled request would inflate the depth for good.
        using var recorded = new FlowScopedMeasurements(DispatchConventions.QueueDepth);
        var channel = new InstrumentedDispatchChannel(TestQueue);

        var enqueue = () => channel.EnqueueAsync("a", new CancellationToken(canceled: true)).AsTask();

        await enqueue.Should().ThrowAsync<OperationCanceledException>();
        recorded.Values(DispatchConventions.QueueDepth, DispatchConventions.Queue, TestQueue).Sum().Should().Be(0);
    }

    [Theory]
    [InlineData(DispatchConventions.BundleRunQueue)]
    [InlineData(DispatchConventions.WorkflowRunQueue)]
    [InlineData(DispatchConventions.ChangeProposalQueue)]
    public async Task EachDispatchQueue_ReportsUnderItsOwnName(string queueName)
    {
        // Three queues feed three dispatchers. A queue reporting under another's name would put one
        // dispatcher's backlog on the other's chart.
        using var recorded = new FlowScopedMeasurements(DispatchConventions.QueueDepth, DispatchConventions.QueueWait);
        var (enqueue, dequeue) = QueueFor(queueName);

        await enqueue("item", CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in dequeue(cts.Token))
            break;

        recorded.Values(DispatchConventions.QueueDepth, DispatchConventions.Queue, queueName).Should().Equal(1, -1);
        recorded.Values(DispatchConventions.QueueWait, DispatchConventions.Queue, queueName).Should().ContainSingle();
    }

    private static (Func<string, CancellationToken, ValueTask> Enqueue, Func<CancellationToken, IAsyncEnumerable<string>> Dequeue)
        QueueFor(string queueName)
    {
        switch (queueName)
        {
            case DispatchConventions.BundleRunQueue:
                IBundleRunDispatchQueue bundles = new InMemoryBundleRunDispatchQueue();
                return (bundles.EnqueueAsync, bundles.DequeueAllAsync);
            case DispatchConventions.WorkflowRunQueue:
                IRunDispatchQueue runs = new InMemoryRunDispatchQueue();
                return (runs.EnqueueAsync, runs.DequeueAllAsync);
            default:
                IChangeProposalDispatchQueue changes = new InMemoryChangeProposalDispatchQueue();
                return (changes.EnqueueAsync, changes.DequeueAllAsync);
        }
    }

    private static async Task<List<string>> TakeAsync(InstrumentedDispatchChannel channel, int count)
    {
        var taken = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var id in channel.DequeueAllAsync(cts.Token))
        {
            taken.Add(id);
            if (taken.Count == count)
                break;
        }

        return taken;
    }
}

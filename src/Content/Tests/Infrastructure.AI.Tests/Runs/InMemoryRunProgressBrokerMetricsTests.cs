using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Helpers;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// <see cref="InMemoryRunProgressBroker"/>'s count of open workflow progress streams.
/// </summary>
/// <remarks>
/// The count is what shows an operator how close the host is to its stream cap. One that misses a release
/// climbs until the chart says the host is full while it is idle; one that counts a refused stream says a
/// caller is being served when it was turned away.
/// </remarks>
public sealed class InMemoryRunProgressBrokerMetricsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    private InMemoryRunProgressBroker BuildSut(int maxStreams = 64)
    {
        var config = new AppConfig();
        config.AI.WorkflowSubmission.MaxConcurrentProgressStreams = maxStreams;
        config.AI.WorkflowSubmission.MaxProgressStreamsPerOwner = maxStreams;
        return new InMemoryRunProgressBroker(new StaticOptionsMonitor<AppConfig>(config), _time);
    }

    [Fact]
    public void AGrantedStream_IsCountedUntilItIsDisposed_AndOnlyOnce()
    {
        using var recorded = new FlowScopedMeasurements(DispatchConventions.ProgressStreamsActive);
        var sut = BuildSut();

        var subscription = sut.Subscribe("job-1", "alice", "acme")!;
        recorded.Values(DispatchConventions.ProgressStreamsActive).Sum().Should().Be(1);

        subscription.Dispose();
        subscription.Dispose();

        recorded.Values(DispatchConventions.ProgressStreamsActive).Should().Equal(new double[] { 1, -1 },
            "a second dispose releases nothing, so it must not count the stream down again");
    }

    [Fact]
    public void ARefusedStream_IsNotCounted()
    {
        using var recorded = new FlowScopedMeasurements(DispatchConventions.ProgressStreamsActive);
        var sut = BuildSut(maxStreams: 1);

        using var granted = sut.Subscribe("job-1", "alice", "acme")!;
        var refused = sut.Subscribe("job-2", "bob", "acme");

        refused.Should().BeNull("the host cap is one stream");
        recorded.Values(DispatchConventions.ProgressStreamsActive).Should().Equal(1);
    }
}

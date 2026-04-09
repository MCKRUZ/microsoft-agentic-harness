using Application.Common.Interfaces.MediatR;
using Application.Common.MediatRBehaviors;
using Domain.Common.Config;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

public class TimeoutBehaviorTests
{
    private record FastRequest : IRequest<string>;

    private record TimeoutRequest : IRequest<string>, IHasTimeout
    {
        public TimeSpan? Timeout { get; init; }
    }

    private static IOptionsMonitor<AgentConfig> CreateConfigMonitor(int timeoutSec = 30)
    {
        var monitor = new Mock<IOptionsMonitor<AgentConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(new AgentConfig
        {
            DefaultRequestTimeoutSec = timeoutSec
        });
        return monitor.Object;
    }

    [Fact]
    public async Task Handle_FastRequest_CompletesNormally()
    {
        var behavior = new TimeoutBehavior<FastRequest, string>(
            CreateConfigMonitor(),
            NullLogger<TimeoutBehavior<FastRequest, string>>.Instance);

        var result = await behavior.Handle(
            new FastRequest(),
            () => Task.FromResult("fast-result"),
            CancellationToken.None);

        result.Should().Be("fast-result");
    }

    [Fact]
    public async Task Handle_SlowRequest_ThrowsTimeoutException()
    {
        var behavior = new TimeoutBehavior<FastRequest, string>(
            CreateConfigMonitor(timeoutSec: 1),
            NullLogger<TimeoutBehavior<FastRequest, string>>.Instance);

        var act = () => behavior.Handle(
            new FastRequest(),
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return "too-slow";
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*exceeded*timeout*");
    }

    [Fact]
    public async Task Handle_RequestWithCustomTimeout_UsesCustomTimeout()
    {
        var behavior = new TimeoutBehavior<TimeoutRequest, string>(
            CreateConfigMonitor(timeoutSec: 1),
            NullLogger<TimeoutBehavior<TimeoutRequest, string>>.Instance);

        // Custom timeout of 5 seconds - should complete a 100ms task easily
        var result = await behavior.Handle(
            new TimeoutRequest { Timeout = TimeSpan.FromSeconds(5) },
            async () =>
            {
                await Task.Delay(100);
                return "custom-timeout-ok";
            },
            CancellationToken.None);

        result.Should().Be("custom-timeout-ok");
    }

    [Fact]
    public async Task Handle_CancellationRequested_PropagatesCancellation()
    {
        var behavior = new TimeoutBehavior<FastRequest, string>(
            CreateConfigMonitor(),
            NullLogger<TimeoutBehavior<FastRequest, string>>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => behavior.Handle(
            new FastRequest(),
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                return "cancelled";
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

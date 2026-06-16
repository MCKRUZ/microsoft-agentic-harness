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

    [Fact]
    public async Task Handle_Timeout_CancelsHandlerCooperativelyViaAmbientToken()
    {
        // Regression for solution-review finding #27: on timeout the behavior must CANCEL the
        // running handler (cooperative cancellation), not merely abandon it and let it keep
        // committing side effects. A handler that observes AmbientTimeoutToken must see cancellation
        // when the configured 1s deadline elapses.
        var behavior = new TimeoutBehavior<FastRequest, string>(
            CreateConfigMonitor(timeoutSec: 1),
            NullLogger<TimeoutBehavior<FastRequest, string>>.Instance);

        // Signals the terminal state the handler actually reached, set deterministically inside the
        // handler so the assertion does not race the abandoned continuation.
        var handlerOutcome = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var act = () => behavior.Handle(
            new FastRequest(),
            async () =>
            {
                var timeoutToken = TimeoutBehavior<FastRequest, string>.AmbientTimeoutToken;
                try
                {
                    // Handler honors its timeout-aware token. When the deadline elapses the
                    // behavior cancels it, so this await throws rather than running to completion.
                    await Task.Delay(TimeSpan.FromSeconds(30), timeoutToken);
                }
                catch (OperationCanceledException)
                {
                    handlerOutcome.TrySetResult("cancelled");
                    throw;
                }

                // Only reached if the handler was abandoned (left running) instead of cancelled.
                handlerOutcome.TrySetResult("side-effect-committed");
                return "should-not-commit";
            },
            CancellationToken.None);

        // The caller still sees the timeout surfaced.
        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*exceeded*timeout*");

        // The handler was cooperatively cancelled, not left running to commit its side effect.
        var outcome = await handlerOutcome.Task.WaitAsync(TimeSpan.FromSeconds(5));
        outcome.Should().Be("cancelled",
            "the timed-out handler must observe cancellation via AmbientTimeoutToken rather than "
            + "running past its deadline to commit side effects");
    }

    [Fact]
    public void AmbientTimeoutToken_OutsideRequestScope_ReturnsNone()
    {
        TimeoutBehavior<FastRequest, string>.AmbientTimeoutToken
            .Should().Be(CancellationToken.None);
    }
}

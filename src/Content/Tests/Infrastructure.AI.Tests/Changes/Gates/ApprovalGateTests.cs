using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes.Gates;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Changes.Gates;

public sealed class ApprovalGateTests
{
    private sealed class RecordingRouter : IChangeApprovalRouter
    {
        public int RouteCount { get; private set; }
        public ChangeProposal? LastProposal { get; private set; }

        public Task RouteAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            RouteCount++;
            LastProposal = proposal;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRouter : IChangeApprovalRouter
    {
        public Task RouteAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("escalation service down");
    }

    private static GateContext Ctx(int attempt = 1) => new()
    {
        Mode = OrchestratorMode.Live,
        AttemptCount = attempt,
        EvaluatedAt = TestProposals.DefaultTime,
        CorrelationId = "corr-1"
    };

    [Fact]
    public async Task EvaluateAsync_HappyPath_RoutesAndReturnsDefer()
    {
        var router = new RecordingRouter();
        var sut = new ApprovalGate(router, NullLogger<ApprovalGate>.Instance);
        var proposal = TestProposals.NewProposal();

        var result = await sut.EvaluateAsync(proposal, Ctx(), CancellationToken.None);

        router.RouteCount.Should().Be(1);
        router.LastProposal.Should().Be(proposal);
        result.Action.Should().Be(GateAction.Defer);
        result.RetryAfter.Should().Be(ApprovalGate.DefaultRetryInterval);
    }

    [Fact]
    public async Task EvaluateAsync_RouterThrows_ReturnsFailNotThrow()
    {
        var sut = new ApprovalGate(new ThrowingRouter(), NullLogger<ApprovalGate>.Instance);

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        // The router's exception message ("escalation service down") must NOT reach
        // the persisted Reason — only the scrubbed code + exception type survive.
        result.Reason.Should().NotContain("escalation service down");
        result.Reason.Should().Be(
            $"{ApprovalGate.RoutingFailedReasonCode}: {nameof(InvalidOperationException)}");
    }

    [Fact]
    public async Task EvaluateAsync_DeferReasonIncludesAttemptCount()
    {
        var router = new RecordingRouter();
        var sut = new ApprovalGate(router, NullLogger<ApprovalGate>.Instance);

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(attempt: 3), CancellationToken.None);

        result.Reason.Should().Contain("attempt 3");
    }

    [Fact]
    public async Task EvaluateAsync_CancellationPropagates()
    {
        var router = new CancellingRouter();
        var sut = new ApprovalGate(router, NullLogger<ApprovalGate>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CancellingRouter : IChangeApprovalRouter
    {
        public Task RouteAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}

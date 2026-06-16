using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes.Gates;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Changes.Gates;

/// <summary>
/// Regression coverage for the 2026-06-11 solution-review finding that the
/// built-in gates leaked raw dependency exception messages into the
/// <see cref="GateResult.Reason"/> field. The orchestrator copies that Reason
/// verbatim into the proposal's <c>History</c> (returned to callers) and into the
/// <c>changes.jsonl</c> audit file. <see cref="ApprovalGate"/>'s router and
/// <see cref="MergeGate"/>'s applier call HTTP/Azure services whose exceptions
/// routinely embed request URLs with SAS tokens or query-string credentials;
/// those must never reach a persisted sink. The fix records a stable scrubbed
/// code plus the exception type only, with the full exception captured via
/// structured logging. These tests are the sibling guard to
/// <see cref="ChangeProposalOrchestratorSolutionReviewFixTests"/>.
/// </summary>
public sealed class GateExceptionMessageScrubbingTests
{
    private const string LeakySecret =
        "sig=abc123SECRET-SAS-TOKEN&se=2026-06-11";

    private static readonly string SecretBearingMessage =
        $"GET https://store.blob.core.windows.net/x?{LeakySecret} failed (403)";

    private static GateContext Ctx() => new()
    {
        Mode = OrchestratorMode.Live,
        AttemptCount = 1,
        EvaluatedAt = TestProposals.DefaultTime,
        CorrelationId = "corr-1"
    };

    [Fact]
    public async Task EvaluateAsync_RouterThrowsWithCredentialInMessage_ScrubsMessageFromReason()
    {
        var sut = new ApprovalGate(
            new SecretLeakingRouter(),
            NullLogger<ApprovalGate>.Instance);

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().NotContain(LeakySecret);
        result.Reason.Should().Be(
            $"{ApprovalGate.RoutingFailedReasonCode}: {nameof(InvalidOperationException)}");
    }

    [Fact]
    public async Task EvaluateAsync_ApplierThrowsWithCredentialInMessage_ScrubsMessageFromReason()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChangeApplier>(
            ChangeTargetKind.GitRepo,
            new SecretLeakingApplier(ChangeTargetKind.GitRepo));
        var sut = new MergeGate(services.BuildServiceProvider(), NullLogger<MergeGate>.Instance);

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().NotContain(LeakySecret);
        result.Reason.Should().Be(
            $"{MergeGate.ApplierThrewReasonCode}: {nameof(InvalidOperationException)}");
    }

    /// <summary>
    /// Stand-in for an HTTP-backed approval router whose exception text embeds a
    /// credential — the exact leak class this regression test guards against.
    /// </summary>
    private sealed class SecretLeakingRouter : IChangeApprovalRouter
    {
        public Task RouteAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException(SecretBearingMessage);
    }

    /// <summary>
    /// Stand-in for a cloud-SDK-backed applier whose exception text embeds a
    /// credential — the exact leak class this regression test guards against.
    /// </summary>
    private sealed class SecretLeakingApplier(ChangeTargetKind kind) : IChangeApplier
    {
        public ChangeTargetKind TargetKind { get; } = kind;
        public Task<ChangeApplyResult> ApplyAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException(SecretBearingMessage);
    }
}

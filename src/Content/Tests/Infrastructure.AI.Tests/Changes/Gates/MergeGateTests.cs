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

public sealed class MergeGateTests
{
    private sealed class ScriptedApplier(ChangeTargetKind kind, ChangeApplyResult result) : IChangeApplier
    {
        public ChangeTargetKind TargetKind { get; } = kind;
        public int InvocationCount { get; private set; }
        public Task<ChangeApplyResult> ApplyAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingApplier(ChangeTargetKind kind) : IChangeApplier
    {
        public ChangeTargetKind TargetKind { get; } = kind;
        public Task<ChangeApplyResult> ApplyAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("applier exploded");
    }

    private static MergeGate Build(params IChangeApplier[] appliers)
    {
        var services = new ServiceCollection();
        foreach (var applier in appliers)
        {
            services.AddKeyedSingleton(applier.TargetKind, applier);
        }
        return new MergeGate(services.BuildServiceProvider(), NullLogger<MergeGate>.Instance);
    }

    private static GateContext Ctx(OrchestratorMode mode = OrchestratorMode.Live) => new()
    {
        Mode = mode,
        AttemptCount = 1,
        EvaluatedAt = TestProposals.DefaultTime,
        CorrelationId = "corr-1"
    };

    [Fact]
    public async Task EvaluateAsync_ShadowMode_ShortCircuitsWithoutInvokingApplier()
    {
        var applier = new ScriptedApplier(ChangeTargetKind.GitRepo, ChangeApplyResult.Succeeded("commit-abc"));
        var sut = Build(applier);

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(OrchestratorMode.Shadow), CancellationToken.None);

        result.Action.Should().Be(GateAction.Pass);
        result.Reason.Should().Contain("shadow");
        applier.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task EvaluateAsync_LiveModeWithSuccessfulApplier_ReturnsPassWithReference()
    {
        var applier = new ScriptedApplier(ChangeTargetKind.GitRepo, ChangeApplyResult.Succeeded("commit-abc", "1 commit pushed"));
        var sut = Build(applier);

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(OrchestratorMode.Live), CancellationToken.None);

        result.Action.Should().Be(GateAction.Pass);
        result.Reason.Should().Contain("commit-abc");
        applier.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_LiveModeWithFailedApplier_ReturnsFailWithReason()
    {
        var applier = new ScriptedApplier(ChangeTargetKind.GitRepo, ChangeApplyResult.Failed("branch advanced since proposal"));
        var sut = Build(applier);

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(OrchestratorMode.Live), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("branch advanced");
    }

    [Fact]
    public async Task EvaluateAsync_NoApplierRegistered_ReturnsFailWithDirective()
    {
        var sut = Build();

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(OrchestratorMode.Live), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("No IChangeApplier registered");
        result.Reason.Should().Contain("GitRepo");
    }

    [Fact]
    public async Task EvaluateAsync_ApplierForWrongTargetKind_ReturnsFail()
    {
        // Applier registered for KubernetesResource but proposal is GitRepo.
        var sut = Build(new ScriptedApplier(ChangeTargetKind.KubernetesResource, ChangeApplyResult.Succeeded("ref")));

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(OrchestratorMode.Live), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("GitRepo");
    }

    [Fact]
    public async Task EvaluateAsync_ApplierThrows_ReturnsFailNotThrow()
    {
        var sut = Build(new ThrowingApplier(ChangeTargetKind.GitRepo));

        var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(OrchestratorMode.Live), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        // The applier's exception message ("applier exploded") must NOT reach the
        // persisted Reason — only the scrubbed code + exception type survive.
        result.Reason.Should().NotContain("applier exploded");
        result.Reason.Should().Be(
            $"{MergeGate.ApplierThrewReasonCode}: {nameof(InvalidOperationException)}");
    }
}

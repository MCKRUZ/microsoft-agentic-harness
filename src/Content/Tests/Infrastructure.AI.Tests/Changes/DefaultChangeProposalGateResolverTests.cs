using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

public sealed class DefaultChangeProposalGateResolverTests
{
    [Theory]
    [InlineData(BlastRadius.Low)]
    [InlineData(BlastRadius.Medium)]
    [InlineData(BlastRadius.High)]
    [InlineData(BlastRadius.Critical)]
    public void NonTrivialRadius_IncludesApprovalGate(BlastRadius radius)
    {
        var sut = new DefaultChangeProposalGateResolver();
        var gates = sut.Resolve(ChangeTargetKind.GitRepo, radius);

        gates.Should().Contain(WellKnownGateKeys.Approval);
        gates.Last().Should().Be(WellKnownGateKeys.Merge);
    }

    [Fact]
    public void TrivialRadius_OmitsApprovalGate()
    {
        var sut = new DefaultChangeProposalGateResolver();
        var gates = sut.Resolve(ChangeTargetKind.GitRepo, BlastRadius.Trivial);

        gates.Should().NotContain(WellKnownGateKeys.Approval);
        gates.Should().Contain(WellKnownGateKeys.SelfValidation);
        gates.Should().Contain(WellKnownGateKeys.Merge);
    }

    [Theory]
    [InlineData(ChangeTargetKind.GitRepo)]
    [InlineData(ChangeTargetKind.KubernetesResource)]
    [InlineData(ChangeTargetKind.IacDeployment)]
    public void AllTargetKinds_GetStandardOrder(ChangeTargetKind kind)
    {
        var sut = new DefaultChangeProposalGateResolver();
        var gates = sut.Resolve(kind, BlastRadius.Medium);

        gates.Should().Equal(
            WellKnownGateKeys.SelfValidation,
            WellKnownGateKeys.Policy,
            WellKnownGateKeys.Approval,
            WellKnownGateKeys.Merge);
    }

    // ─────────────────────────────────────────────────────────────────────
    // PR-4: ResolveWithDecision honours the graded-autonomy decision.
    // ─────────────────────────────────────────────────────────────────────

    private static AutonomyDecisionResult Decision(
        AutonomyDecision decision,
        BlastRadius radius = BlastRadius.Low,
        ChangeTargetKind targetKind = ChangeTargetKind.GitRepo)
        => new(
            Decision: decision,
            Tier: AutonomyLevel.Autonomous,
            BlastRadius: radius,
            TargetKind: targetKind,
            IsStateChange: false,
            Environment: "Test",
            SkillKey: null,
            Reason: "test");

    [Fact]
    public void ResolveWithDecision_NullDecision_FallsBackToStaticRule()
    {
        var sut = new DefaultChangeProposalGateResolver();

        var gates = sut.ResolveWithDecision(ChangeTargetKind.GitRepo, BlastRadius.Medium, decision: null);

        gates.Should().Contain(WellKnownGateKeys.Approval);
    }

    [Fact]
    public void ResolveWithDecision_AutoApprove_OmitsApprovalGate()
    {
        var sut = new DefaultChangeProposalGateResolver();

        var gates = sut.ResolveWithDecision(
            ChangeTargetKind.GitRepo,
            BlastRadius.Medium,
            Decision(AutonomyDecision.AutoApprove, BlastRadius.Medium));

        gates.Should().NotContain(WellKnownGateKeys.Approval);
        gates.Should().Contain(WellKnownGateKeys.Merge);
    }

    [Fact]
    public void ResolveWithDecision_RequiresApproval_IncludesApprovalGate()
    {
        var sut = new DefaultChangeProposalGateResolver();

        var gates = sut.ResolveWithDecision(
            ChangeTargetKind.GitRepo,
            BlastRadius.Low,
            Decision(AutonomyDecision.RequiresApproval, BlastRadius.Low));

        gates.Should().Contain(WellKnownGateKeys.Approval);
    }

    [Fact]
    public void ResolveWithDecision_CriticalRadius_AutoApprove_StillIncludesApproval()
    {
        // Even if the evaluator passes AutoApprove for Critical, the resolver
        // re-asserts the safety invariant.
        var sut = new DefaultChangeProposalGateResolver();

        var gates = sut.ResolveWithDecision(
            ChangeTargetKind.GitRepo,
            BlastRadius.Critical,
            Decision(AutonomyDecision.AutoApprove, BlastRadius.Critical));

        gates.Should().Contain(WellKnownGateKeys.Approval);
    }
}

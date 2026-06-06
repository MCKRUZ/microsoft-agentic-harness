using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
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
}

using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Escalation.Strategies;

public class QuorumApprovalStrategyTests
{
    private readonly IApprovalStrategy _sut = new QuorumApprovalStrategy();

    private static EscalationRequest CreateRequest(string[] approvers, int quorumThreshold) => new()
    {
        EscalationId = Guid.NewGuid(),
        AgentId = "test-agent",
        ToolName = "test-tool",
        Arguments = new Dictionary<string, string>(),
        Description = "Test escalation",
        RiskLevel = RiskLevel.Medium,
        Priority = EscalationPriority.Blocking,
        ApprovalStrategy = ApprovalStrategyType.Quorum,
        Approvers = approvers,
        QuorumThreshold = quorumThreshold,
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static ApproverDecision Approve(string name) => new()
    {
        ApproverName = name,
        Approved = true,
        RespondedAt = DateTimeOffset.UtcNow
    };

    private static ApproverDecision Deny(string name) => new()
    {
        ApproverName = name,
        Approved = false,
        Reason = "Denied",
        RespondedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void EvaluateDecision_QuorumMet_ResolvesApproved()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Approve("alice"), Approve("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void EvaluateDecision_QuorumImpossible_ResolvesDenied()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Deny("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse();
    }

    [Fact]
    public void EvaluateDecision_InsufficientVotes_NotResolved()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Approve("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeFalse();
    }

    [Fact]
    public void EvaluateDecision_EdgeCase_OneOfOne_ResolvesOnFirst()
    {
        var request = CreateRequest(["alice"], quorumThreshold: 1);
        var decisions = new[] { Approve("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void EvaluateDecision_TwoOfThree_MixedOutcomes(
        bool firstApproves, bool secondApproves, bool expectedResolved)
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[]
        {
            firstApproves ? Approve("alice") : Deny("alice"),
            secondApproves ? Approve("bob") : Deny("bob")
        };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().Be(expectedResolved);
    }

    [Fact]
    public void EvaluateDecision_TwoOfThree_TwoDenials_ResolvesDenied()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Deny("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse();
    }

    [Fact]
    public void EvaluateDecision_ThresholdEqualsTotal_BehavesLikeAllOf()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 3);

        var allApproved = _sut.EvaluateDecision(request,
            [Approve("alice"), Approve("bob"), Approve("carol")]);
        allApproved.IsResolved.Should().BeTrue();
        allApproved.IsApproved.Should().BeTrue();

        var oneDenied = _sut.EvaluateDecision(request,
            [Approve("alice"), Deny("bob")]);
        oneDenied.IsResolved.Should().BeTrue();
        oneDenied.IsApproved.Should().BeFalse();
    }

    [Fact]
    public void StrategyType_ReturnsQuorum()
    {
        _sut.StrategyType.Should().Be(ApprovalStrategyType.Quorum);
    }
}

using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Escalation.Strategies;

public class AnyOfApprovalStrategyTests
{
    private readonly IApprovalStrategy _sut = new AnyOfApprovalStrategy();

    private static EscalationRequest CreateRequest(params string[] approvers) => new()
    {
        EscalationId = Guid.NewGuid(),
        AgentId = "test-agent",
        ToolName = "test-tool",
        Arguments = new Dictionary<string, string>(),
        Description = "Test escalation",
        RiskLevel = RiskLevel.Medium,
        Priority = EscalationPriority.Blocking,
        ApprovalStrategy = ApprovalStrategyType.AnyOf,
        Approvers = approvers,
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
    public void EvaluateDecision_SingleApproval_ResolvesApproved()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeTrue();
        result.PendingApprovers.Should().BeEquivalentTo(["bob", "carol"]);
    }

    [Fact]
    public void EvaluateDecision_SingleDenial_ResolvesDenied()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse();
    }

    [Fact]
    public void EvaluateDecision_NoDecisions_NotResolved()
    {
        var request = CreateRequest("alice", "bob", "carol");

        var result = _sut.EvaluateDecision(request, Array.Empty<ApproverDecision>());

        result.IsResolved.Should().BeFalse();
        result.IsApproved.Should().BeFalse();
        result.PendingApprovers.Should().BeEquivalentTo(["alice", "bob", "carol"]);
    }

    [Fact]
    public void EvaluateDecision_MultipleApprovers_FirstResponseWins()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void StrategyType_ReturnsAnyOf()
    {
        _sut.StrategyType.Should().Be(ApprovalStrategyType.AnyOf);
    }
}

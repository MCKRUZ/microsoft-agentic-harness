using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Escalation.Strategies;

public class AllOfApprovalStrategyTests
{
    private readonly IApprovalStrategy _sut = new AllOfApprovalStrategy();

    private static EscalationRequest CreateRequest(params string[] approvers) => new()
    {
        EscalationId = Guid.NewGuid(),
        AgentId = "test-agent",
        ToolName = "test-tool",
        Arguments = new Dictionary<string, string>(),
        Description = "Test escalation",
        RiskLevel = RiskLevel.Medium,
        Priority = EscalationPriority.Blocking,
        ApprovalStrategy = ApprovalStrategyType.AllOf,
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
    public void EvaluateDecision_AllApproved_ResolvesApproved()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Approve("bob"), Approve("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeTrue();
        result.PendingApprovers.Should().BeEmpty();
    }

    [Fact]
    public void EvaluateDecision_SingleDenialAmongMultiple_ResolvesDeniedImmediately()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse();
    }

    [Fact]
    public void EvaluateDecision_PartialApprovals_NotResolved()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Approve("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeFalse();
        result.PendingApprovers.Should().BeEquivalentTo(["carol"]);
    }

    [Fact]
    public void EvaluateDecision_SingleApprover_ApprovesImmediately()
    {
        var request = CreateRequest("alice");
        var decisions = new[] { Approve("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void StrategyType_ReturnsAllOf()
    {
        _sut.StrategyType.Should().Be(ApprovalStrategyType.AllOf);
    }

    // ---- roster-membership enforcement (security fix) ----

    [Fact]
    public void EvaluateDecision_OnlyNonRosterDecision_DoesNotResolve()
    {
        var request = CreateRequest("alice", "bob");

        var result = _sut.EvaluateDecision(request, new[] { Deny("mallory") });

        result.IsResolved.Should().BeFalse("a decision from an identity outside the roster must not resolve an AllOf escalation");
        result.PendingApprovers.Should().BeEquivalentTo(["alice", "bob"]);
    }

    [Fact]
    public void EvaluateDecision_NonRosterDenial_DoesNotOverrideRosterApproval()
    {
        var request = CreateRequest("alice");

        var result = _sut.EvaluateDecision(request, new[] { Approve("alice"), Deny("mallory") });

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeTrue("a non-roster denial must not override the sole listed approver's approval");
    }

    // ---- empty-roster fail-closed (security fix) ----

    [Fact]
    public void EvaluateDecision_EmptyRosterWithDecision_FailsClosed()
    {
        var request = CreateRequest();

        var result = _sut.EvaluateDecision(request, new[] { Approve("mallory") });

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse("an escalation with no approvers must never auto-approve");
    }

    [Fact]
    public void EvaluateDecision_EmptyRosterNoDecisions_FailsClosed()
    {
        var request = CreateRequest();

        var result = _sut.EvaluateDecision(request, Array.Empty<ApproverDecision>());

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse("an empty roster must fail closed, not auto-approve as vacuously unanimous");
    }
}

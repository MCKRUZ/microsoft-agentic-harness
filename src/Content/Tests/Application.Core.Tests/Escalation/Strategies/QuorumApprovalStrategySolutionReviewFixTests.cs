using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// Regression tests for the 2026-06-11 solution review fixes to <see cref="QuorumApprovalStrategy"/>:
/// (idx 3) non-positive <c>QuorumThreshold</c> must fail closed rather than auto-approve, and
/// (idx 29) decisions from identities outside <c>request.Approvers</c> must not be counted.
/// </summary>
public class QuorumApprovalStrategySolutionReviewFixTests
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

    // ---- idx 3: non-positive threshold fails closed (was: auto-approved on first decision) ----

    [Fact]
    public void EvaluateDecision_ZeroThresholdWithDenial_ResolvesDenied()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 0);
        var decisions = new[] { Deny("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse("a non-positive quorum threshold is a misconfigured gate and must fail closed");
    }

    [Fact]
    public void EvaluateDecision_ZeroThresholdWithApproval_ResolvesDenied()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 0);
        var decisions = new[] { Approve("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse("a misconfigured (default) threshold must never auto-approve");
    }

    [Fact]
    public void EvaluateDecision_NegativeThreshold_ResolvesDenied()
    {
        var request = CreateRequest(["alice"], quorumThreshold: -1);

        var result = _sut.EvaluateDecision(request, [Approve("alice")]);

        result.IsResolved.Should().BeTrue();
        result.IsApproved.Should().BeFalse();
    }

    // ---- idx 29: only listed approvers' votes count ----

    [Fact]
    public void EvaluateDecision_NonListedApproversVoting_DoesNotSatisfyQuorum()
    {
        // Approvers = [A,B,C], threshold 2. Two approvals from non-listed D and E.
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Approve("dave"), Approve("erin") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeFalse("votes from identities not in the approver list must be ignored");
        result.PendingApprovers.Should().BeEquivalentTo("alice", "bob", "carol");
    }

    [Fact]
    public void EvaluateDecision_NonListedDenials_DoNotTriggerFalseImpossibility()
    {
        // Approvers = [A,B,C], threshold 2. Three denials from non-listed D,E,F.
        // Old behavior: remainingVotes = 3 - 0 - 3 = 0 => false "quorum impossible" DENIAL.
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Deny("dave"), Deny("erin"), Deny("frank") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeFalse("non-listed denials must not corrupt the remaining-vote math");
        result.PendingApprovers.Should().BeEquivalentTo("alice", "bob", "carol");
    }

    [Fact]
    public void EvaluateDecision_MixedListedAndNonListed_CountsOnlyListed()
    {
        // Listed approval from alice + stray approval from dave should NOT meet threshold 2.
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Approve("alice"), Approve("dave") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeFalse();
        result.PendingApprovers.Should().BeEquivalentTo("bob", "carol");
    }
}

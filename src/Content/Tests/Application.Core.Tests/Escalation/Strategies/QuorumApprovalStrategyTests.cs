using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;
using static Application.Core.Tests.Escalation.Strategies.ApproverDecisionFixtures;

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

    [Fact]
    public void EvaluateDecision_QuorumMet_ResolvesApproved()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Approve("alice"), Approve("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Approve);
    }

    [Fact]
    public void EvaluateDecision_QuorumImpossible_ResolvesDenied()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Deny("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
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
        result.Verdict.Should().Be(ApproverVerdict.Approve);
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
        result.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void EvaluateDecision_ThresholdEqualsTotal_BehavesLikeAllOf()
    {
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 3);

        var allApproved = _sut.EvaluateDecision(request,
            [Approve("alice"), Approve("bob"), Approve("carol")]);
        allApproved.IsResolved.Should().BeTrue();
        allApproved.Verdict.Should().Be(ApproverVerdict.Approve);

        var oneDenied = _sut.EvaluateDecision(request,
            [Approve("alice"), Deny("bob")]);
        oneDenied.IsResolved.Should().BeTrue();
        oneDenied.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void StrategyType_ReturnsQuorum()
    {
        _sut.StrategyType.Should().Be(ApprovalStrategyType.Quorum);
    }

    // ---- three-way verdict matrix (#321) ----

    [Fact]
    public void EvaluateDecision_OneDenyOneReviseOneApprove_QuorumImpossible_ResolvesDenied()
    {
        // Threshold 2 of 3: with one denial, one revise, and one approve all cast, quorum for
        // Approve is mathematically impossible (max reachable approve count is 1). A denial is
        // present, so denial takes precedence over the revise in the impossible-quorum outcome.
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Deny("alice"), Revise("bob"), Approve("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void EvaluateDecision_AllThreeRevise_QuorumImpossible_ResolvesRevised()
    {
        // Mutation control for the test above: with the denial replaced by a third revise (no
        // denial anywhere), the same impossible-quorum outcome resolves Revised instead of Denied.
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Revise("alice"), Revise("bob"), Revise("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Revise);
    }

    [Fact]
    public void EvaluateDecision_ApproveThresholdMetWithReviseAlsoPresent_ResolvesApproved()
    {
        // Meeting the approval threshold wins even with a revise also cast. Consistency with
        // existing Quorum behaviour demands it: a single deny cannot block a met quorum today
        // (see EvaluateDecision_ThresholdEqualsTotal_BehavesLikeAllOf below, where two approvals
        // never lose to a later denial once threshold is already met at 2-of-3), so a single
        // revise must not gain a veto power a denier does not have either.
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Approve("alice"), Approve("bob"), Revise("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Approve);
    }

    // ---- undefined verdict fails closed in the vote arithmetic (code-review regression) ----

    [Fact]
    public void EvaluateDecision_UndefinedVerdictAmongResponses_CountsAsDenyInRemainingVoteMath()
    {
        // Before the fix, tally.Total (used to compute remainingVotes) silently excluded a
        // decision with an undefined verdict, so a fully-responded roster of [Deny, <undefined>]
        // under threshold 2 was mis-counted as only 1 response with 1 remaining vote still
        // possible — the escalation stayed unresolved indefinitely (pending nobody, since
        // ApproverRoster.Scope already shows nobody pending) instead of resolving denied the
        // instant it became mathematically impossible to reach quorum.
        var request = CreateRequest(["alice", "bob"], quorumThreshold: 2);
        var decisions = new[]
        {
            Deny("alice"),
            new ApproverDecision
            {
                ApproverName = "bob",
                Verdict = (ApproverVerdict)42,
                RespondedAt = DateTimeOffset.UtcNow
            }
        };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue("both approvers have responded, so quorum is already decided one way or the other");
        result.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void EvaluateDecision_OneApproveTwoRevise_ThresholdNotMet_ResolvesRevised()
    {
        // Mutation control for the test above: with only ONE approval cast (threshold not met by
        // approvals alone) and two revises, the outcome flips to Revised — proving the prior
        // test's Approved result came from the threshold being met, not from revise being unable
        // to ever win against a mixed roster.
        var request = CreateRequest(["alice", "bob", "carol"], quorumThreshold: 2);
        var decisions = new[] { Approve("alice"), Revise("bob"), Revise("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Revise);
    }
}

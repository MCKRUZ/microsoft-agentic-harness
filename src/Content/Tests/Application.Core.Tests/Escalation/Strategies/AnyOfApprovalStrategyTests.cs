using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;
using static Application.Core.Tests.Escalation.Strategies.ApproverDecisionFixtures;

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

    [Fact]
    public void EvaluateDecision_SingleApproval_ResolvesApproved()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Approve);
        result.PendingApprovers.Should().BeEquivalentTo(["bob", "carol"]);
    }

    [Fact]
    public void EvaluateDecision_SingleDenial_ResolvesDenied()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void EvaluateDecision_NoDecisions_NotResolved()
    {
        var request = CreateRequest("alice", "bob", "carol");

        var result = _sut.EvaluateDecision(request, Array.Empty<ApproverDecision>());

        result.IsResolved.Should().BeFalse();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
        result.PendingApprovers.Should().BeEquivalentTo(["alice", "bob", "carol"]);
    }

    // ---- precedence, not arrival order (deny > revise > approve) ----
    //
    // AnyOf used to pick the earliest decision by RespondedAt, which made a governance outcome
    // depend on a timestamp tie or clock skew: two decisions can land in the collected set before
    // the first evaluation runs. Precedence over the whole scoped set is deterministic regardless
    // of which decision was submitted, or constructed, first. The next two tests are the control
    // pair for that fix: same two verdicts, reversed order, same result.

    [Fact]
    public void EvaluateDecision_DenyListedBeforeApprove_ResolvesDenied()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Deny("bob"), Approve("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void EvaluateDecision_ApproveListedBeforeDeny_StillResolvesDenied()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(
            ApproverVerdict.Deny,
            "deny takes precedence over approve regardless of which decision arrived, or was listed, first");
    }

    [Fact]
    public void EvaluateDecision_ApproveAndRevise_ResolvesRevised()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Revise("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Revise, "revise takes precedence over approve");
    }

    [Fact]
    public void EvaluateDecision_ReviseAndDeny_ResolvesDenied()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Revise("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny, "a hard no is never softened into try-again");
    }

    // ---- undefined verdict fails closed, does not crash (code-review regression) ----

    [Fact]
    public void EvaluateDecision_UndefinedVerdict_ResolvesDenied_DoesNotThrow()
    {
        // Before the fix, VerdictTally's switch had no default arm: an undefined verdict was
        // counted nowhere, so Resolve() returned null and tally.Resolve()!.Value threw.
        var request = CreateRequest("alice");
        var decisions = new[]
        {
            new ApproverDecision
            {
                ApproverName = "alice",
                Verdict = (ApproverVerdict)42,
                RespondedAt = DateTimeOffset.UtcNow
            }
        };

        var act = () => _sut.EvaluateDecision(request, decisions);

        act.Should().NotThrow();
        var result = _sut.EvaluateDecision(request, decisions);
        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void EvaluateDecision_SingleRevise_ResolvesRevised()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Revise("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Revise);
    }

    [Fact]
    public void StrategyType_ReturnsAnyOf()
    {
        _sut.StrategyType.Should().Be(ApprovalStrategyType.AnyOf);
    }

    // ---- roster-membership enforcement (security fix) ----

    [Fact]
    public void EvaluateDecision_OnlyNonRosterApproval_DoesNotResolve()
    {
        var request = CreateRequest("alice", "bob", "carol");

        var result = _sut.EvaluateDecision(request, new[] { Approve("mallory") });

        result.IsResolved.Should().BeFalse("a decision from an identity outside the approver roster must not resolve an AnyOf escalation");
        result.PendingApprovers.Should().BeEquivalentTo(["alice", "bob", "carol"]);
    }

    [Fact]
    public void EvaluateDecision_NonRosterEarliestDecision_Ignored_RosterDecisionWins()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var now = DateTimeOffset.UtcNow;
        var decisions = new[]
        {
            new ApproverDecision { ApproverName = "mallory", Verdict = ApproverVerdict.Deny, Reason = "hijack", RespondedAt = now },
            new ApproverDecision { ApproverName = "alice", Verdict = ApproverVerdict.Approve, RespondedAt = now.AddSeconds(1) }
        };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Approve, "a non-roster decision (even the earliest) must be ignored; only alice's vote counts");
    }
}

using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;
using static Application.Core.Tests.Escalation.Strategies.ApproverDecisionFixtures;

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

    [Fact]
    public void EvaluateDecision_AllApproved_ResolvesApproved()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Approve("bob"), Approve("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Approve);
        result.PendingApprovers.Should().BeEmpty();
    }

    [Fact]
    public void EvaluateDecision_SingleDenialAmongMultiple_ResolvesDeniedImmediately()
    {
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
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
        result.Verdict.Should().Be(ApproverVerdict.Approve);
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
        result.Verdict.Should().Be(ApproverVerdict.Approve, "a non-roster denial must not override the sole listed approver's approval");
    }

    // ---- empty-roster fail-closed (security fix) ----

    [Fact]
    public void EvaluateDecision_EmptyRosterWithDecision_FailsClosed()
    {
        var request = CreateRequest();

        var result = _sut.EvaluateDecision(request, new[] { Approve("mallory") });

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny, "an escalation with no approvers must never auto-approve");
    }

    [Fact]
    public void EvaluateDecision_EmptyRosterNoDecisions_FailsClosed()
    {
        var request = CreateRequest();

        var result = _sut.EvaluateDecision(request, Array.Empty<ApproverDecision>());

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny, "an empty roster must fail closed, not auto-approve as vacuously unanimous");
    }

    // ---- revise does NOT short-circuit while approvers are pending, unlike deny ----

    [Fact]
    public void EvaluateDecision_OneRevisesOthersPending_NotResolved()
    {
        // A pending approver may yet deny. Resolving Revise here — the way a deny resolves
        // immediately — would soften that possible hard no into "try again" before it had a
        // chance to land.
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Revise("alice") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeFalse();
        result.PendingApprovers.Should().BeEquivalentTo(["bob", "carol"]);
    }

    [Fact]
    public void EvaluateDecision_ReviseThenDeny_ResolvesDeniedNotRevised()
    {
        // Mutation control for the test above: once the LAST pending vote lands as a deny, the
        // escalation resolves — and it resolves Denied, not Revised, because deny outranks revise.
        var request = CreateRequest("alice", "bob");
        var decisions = new[] { Revise("alice"), Deny("bob") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void EvaluateDecision_AllReviseOrApprove_NoDenials_ResolvesRevised()
    {
        // Every approver has responded, nobody denied, at least one asked to revise: the
        // escalation resolves Revised, not Approved — unanimity alone is not enough when a
        // revision was requested.
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Revise("bob"), Approve("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Revise);
    }

    // ---- undefined verdict fails closed, does not silently vanish (code-review regression) ----

    [Fact]
    public void EvaluateDecision_UndefinedVerdictAmongApprovals_ResolvesDenied_NotApproved()
    {
        // Before the fix, an undefined verdict was counted nowhere by VerdictTally, so a roster
        // of [Approve, <undefined>] looked identical to a roster of [Approve] alone — the second
        // approver's actual response silently disappeared and the escalation resolved Approved
        // as if only one approver had ever been asked.
        var request = CreateRequest("alice", "bob");
        var decisions = new[]
        {
            Approve("alice"),
            new ApproverDecision
            {
                ApproverName = "bob",
                Verdict = (ApproverVerdict)42,
                RespondedAt = DateTimeOffset.UtcNow
            }
        };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Deny, "an undefined verdict must fail closed, never silently vanish into an approval");
    }

    [Fact]
    public void EvaluateDecision_AllApproveNoRevise_ResolvesApproved()
    {
        // Mutation control for the test above: with the sole revise swapped for an approve, the
        // same roster resolves Approved instead.
        var request = CreateRequest("alice", "bob", "carol");
        var decisions = new[] { Approve("alice"), Approve("bob"), Approve("carol") };

        var result = _sut.EvaluateDecision(request, decisions);

        result.IsResolved.Should().BeTrue();
        result.Verdict.Should().Be(ApproverVerdict.Approve);
    }
}

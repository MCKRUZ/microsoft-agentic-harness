using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Escalation;

/// <summary>
/// Tests for <see cref="EscalationRequestInvariants"/> — the runtime chokepoint every
/// escalation request passes through on both creation and rehydration.
/// </summary>
public sealed class EscalationRequestInvariantsTests
{
    private static EscalationRequest CreateValidRequest(
        EscalationPriority priority = EscalationPriority.Blocking,
        EscalationTimeoutAction timeoutAction = EscalationTimeoutAction.DenyAndEscalate,
        ApprovalStrategyType strategy = ApprovalStrategyType.AnyOf) =>
        new()
        {
            EscalationId = Guid.NewGuid(),
            AgentId = "test-agent",
            ToolName = "dangerous-tool",
            Arguments = new Dictionary<string, string>(),
            Description = "Test escalation",
            RiskLevel = RiskLevel.High,
            Priority = priority,
            ApprovalStrategy = strategy,
            Approvers = ["approver-1"],
            TimeoutSeconds = 300,
            TimeoutAction = timeoutAction,
            RequestedAt = DateTimeOffset.UtcNow
        };

    // ===== Critical + Approve-on-timeout =====

    [Fact]
    public void TryValidate_CriticalPriorityWithApproveOnTimeout_IsRejected()
    {
        // The defect this closes: EscalationTimeoutAction.Approve's own XML doc says this
        // pairing "should never" occur and names FluentValidation as the enforcement — which
        // did not exist anywhere in the codebase before this test.
        var request = CreateValidRequest(
            priority: EscalationPriority.Critical,
            timeoutAction: EscalationTimeoutAction.Approve);

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("Critical");
    }

    [Fact]
    public void TryValidate_CriticalPriorityWithDenyAndEscalate_IsAccepted()
    {
        // Mutation control: Critical priority alone must not be rejected — only the pairing
        // with Approve. Every current production caller that raises a Critical escalation uses
        // this exact combination.
        var request = CreateValidRequest(
            priority: EscalationPriority.Critical,
            timeoutAction: EscalationTimeoutAction.DenyAndEscalate);

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }

    [Fact]
    public void TryValidate_BlockingPriorityWithApproveOnTimeout_IsAccepted()
    {
        // Mutation control: Approve-on-timeout alone must not be rejected — the rule is scoped
        // to Critical priority. Approve is a legitimate default for informational/blocking
        // escalations nobody may be watching.
        var request = CreateValidRequest(
            priority: EscalationPriority.Blocking,
            timeoutAction: EscalationTimeoutAction.Approve);

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }

    // ===== Undefined enum values (hand-edited or corrupted durable row) =====

    [Fact]
    public void TryValidate_UndefinedApprovalStrategy_IsRejected()
    {
        var request = CreateValidRequest() with { ApprovalStrategy = (ApprovalStrategyType)99 };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("approval strategy");
    }

    [Fact]
    public void TryValidate_DefinedApprovalStrategy_IsAccepted()
    {
        var request = CreateValidRequest(strategy: ApprovalStrategyType.Quorum) with { QuorumThreshold = 1 };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }

    [Fact]
    public void TryValidate_UndefinedTimeoutAction_IsRejected()
    {
        var request = CreateValidRequest() with { TimeoutAction = (EscalationTimeoutAction)99 };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("timeout action");
    }

    [Fact]
    public void TryValidate_UndefinedPriority_IsRejected()
    {
        var request = CreateValidRequest() with { Priority = (EscalationPriority)99 };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("priority");
    }

    [Fact]
    public void TryValidate_AllDefinedValues_IsAccepted()
    {
        // Mutation control for the three undefined-value checks together: a request built
        // entirely from defined enum members must pass.
        var request = CreateValidRequest();

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }

    // ===== #325 retry attribution: AttemptNumber / PriorFailureReason =====

    [Fact]
    public void TryValidate_AttemptNumberZero_IsRejected()
    {
        var request = CreateValidRequest() with { AttemptNumber = 0 };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("attempt number");
    }

    [Fact]
    public void TryValidate_AttemptNumberNegative_IsRejected()
    {
        var request = CreateValidRequest() with { AttemptNumber = -1 };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("attempt number");
    }

    [Fact]
    public void TryValidate_AttemptNumberOneWithNoPriorFailureReason_IsAccepted()
    {
        // Mutation control: a first attempt (the default shape) must not be rejected by the
        // attempt-number floor.
        var request = CreateValidRequest() with { AttemptNumber = 1, PriorFailureReason = null };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }

    [Fact]
    public void TryValidate_AttemptOneWithPriorFailureReason_IsRejected()
    {
        // A first attempt cannot have failed before — a prior failure reason on attempt 1 is
        // internally incoherent, reachable only via a hand-edited or corrupted durable row.
        var request = CreateValidRequest() with { AttemptNumber = 1, PriorFailureReason = "boom" };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("attempt 1");
    }

    [Fact]
    public void TryValidate_AttemptTwoWithPriorFailureReason_IsAccepted()
    {
        // Mutation control: the coherent retry shape (attempt > 1, reason present) must pass.
        var request = CreateValidRequest() with { AttemptNumber = 2, PriorFailureReason = "boom" };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }

    [Fact]
    public void TryValidate_AttemptTwoWithNoPriorFailureReason_IsAccepted()
    {
        // The deliberately-NOT-rejected shape: an attempt count above 1 with no prior failure
        // reason is what a benign LRU eviction of the failure memory produces (the recall came
        // back null, so AttemptNumber stayed effectively re-derivable as "still a retry" while
        // the reason itself was gone). Rejecting this would fail-close a valid escalation purely
        // because the bounded memory it depends on evicted an entry.
        var request = CreateValidRequest() with { AttemptNumber = 2, PriorFailureReason = null };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }

    [Fact]
    public void TryValidate_PriorFailureReasonExceedsMaxLength_IsRejected()
    {
        var request = CreateValidRequest() with
        {
            AttemptNumber = 2,
            PriorFailureReason = new string('x', EscalationRequestInvariants.MaxPriorFailureReasonLength + 1)
        };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeFalse();
        violation.Should().Contain("exceeds the");
    }

    [Fact]
    public void TryValidate_PriorFailureReasonAtMaxLength_IsAccepted()
    {
        // Boundary control: exactly at the ceiling must still pass — only exceeding it is a
        // violation.
        var request = CreateValidRequest() with
        {
            AttemptNumber = 2,
            PriorFailureReason = new string('x', EscalationRequestInvariants.MaxPriorFailureReasonLength)
        };

        var isValid = EscalationRequestInvariants.TryValidate(request, out var violation);

        isValid.Should().BeTrue();
        violation.Should().BeNull();
    }
}

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
}

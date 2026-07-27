using Application.Core.CQRS.Escalation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Validator tests for the escalation CQRS surface. The approver-name rules are the
/// defense-in-depth backstop behind the controller's fail-closed claim resolution; the reason
/// bounds keep the JSONL audit records sane.
/// </summary>
public sealed class EscalationCqrsValidationTests
{
    private static readonly string LongName = new('a', EscalationValidationRules.MaxApproverNameLength + 1);
    private static readonly string LongReason = new('r', EscalationValidationRules.MaxReasonLength + 1);

    // --- GetPendingEscalationsForApproverQuery ---

    [Fact]
    public void PendingListValidator_ValidQuery_Passes()
    {
        var result = new GetPendingEscalationsForApproverQueryValidator().Validate(
            new GetPendingEscalationsForApproverQuery { ApproverName = "alice@contoso.com" });

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void PendingListValidator_MissingApproverName_Fails(string name)
    {
        var result = new GetPendingEscalationsForApproverQueryValidator().Validate(
            new GetPendingEscalationsForApproverQuery { ApproverName = name });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PendingListValidator_OversizedApproverName_Fails()
    {
        var result = new GetPendingEscalationsForApproverQueryValidator().Validate(
            new GetPendingEscalationsForApproverQuery { ApproverName = LongName });

        result.IsValid.Should().BeFalse();
    }

    // --- GetEscalationQuery ---

    [Fact]
    public void GetValidator_ValidQuery_Passes()
    {
        var result = new GetEscalationQueryValidator().Validate(new GetEscalationQuery
        {
            EscalationId = Guid.NewGuid(),
            ApproverName = "alice@contoso.com"
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void GetValidator_EmptyId_Fails()
    {
        var result = new GetEscalationQueryValidator().Validate(new GetEscalationQuery
        {
            EscalationId = Guid.Empty,
            ApproverName = "alice@contoso.com"
        });

        result.IsValid.Should().BeFalse();
    }

    // --- SubmitEscalationDecisionCommand ---

    [Fact]
    public void DecisionValidator_ValidCommand_Passes()
    {
        var result = new SubmitEscalationDecisionCommandValidator().Validate(
            new SubmitEscalationDecisionCommand
            {
                EscalationId = Guid.NewGuid(),
                ApproverName = "alice@contoso.com",
                Approve = true,
                Reason = null
            });

        result.IsValid.Should().BeTrue("the reason is optional on decisions");
    }

    [Fact]
    public void DecisionValidator_EmptyApproverName_Fails()
    {
        var result = new SubmitEscalationDecisionCommandValidator().Validate(
            new SubmitEscalationDecisionCommand
            {
                EscalationId = Guid.NewGuid(),
                ApproverName = string.Empty,
                Approve = true
            });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void DecisionValidator_OversizedReason_Fails()
    {
        var result = new SubmitEscalationDecisionCommandValidator().Validate(
            new SubmitEscalationDecisionCommand
            {
                EscalationId = Guid.NewGuid(),
                ApproverName = "alice@contoso.com",
                Approve = false,
                Reason = LongReason
            });

        result.IsValid.Should().BeFalse();
    }

    // --- CancelEscalationCommand ---

    [Fact]
    public void CancelValidator_ValidCommand_Passes()
    {
        var result = new CancelEscalationCommandValidator().Validate(new CancelEscalationCommand
        {
            EscalationId = Guid.NewGuid(),
            Reason = "superseded by new plan",
            CancelledBy = "admin@contoso.com"
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CancelValidator_EmptyReason_Fails()
    {
        // Cancellations are administrative force-denials; an unexplained one is not auditable.
        var result = new CancelEscalationCommandValidator().Validate(new CancelEscalationCommand
        {
            EscalationId = Guid.NewGuid(),
            Reason = string.Empty,
            CancelledBy = "admin@contoso.com"
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CancelValidator_EmptyCancelledBy_Fails()
    {
        var result = new CancelEscalationCommandValidator().Validate(new CancelEscalationCommand
        {
            EscalationId = Guid.NewGuid(),
            Reason = "superseded",
            CancelledBy = string.Empty
        });

        result.IsValid.Should().BeFalse();
    }
}

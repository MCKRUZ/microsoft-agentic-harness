using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Tests for <see cref="SubmitEscalationDecisionCommandValidator"/>, the sole validation
/// chokepoint for a decision arriving over HTTP before it reaches the escalation service and the
/// approval strategies.
/// </summary>
public sealed class SubmitEscalationDecisionCommandValidatorTests
{
    private readonly SubmitEscalationDecisionCommandValidator _validator = new();

    private static SubmitEscalationDecisionCommand CreateValidCommand(
        ApproverVerdict? verdict = null, bool approve = true, string? instructions = null) => new()
    {
        EscalationId = Guid.NewGuid(),
        ApproverName = "alice",
        Approve = approve,
        Verdict = verdict,
        Instructions = instructions
    };

    [Fact]
    public void Validate_LegacyApproveOnly_NoErrors()
    {
        var result = _validator.Validate(CreateValidCommand(verdict: null, approve: true));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_VerdictMatchingApprove_NoErrors()
    {
        var result = _validator.Validate(
            CreateValidCommand(verdict: ApproverVerdict.Approve, approve: true));

        result.IsValid.Should().BeTrue();
    }

    // ===== Undefined verdict — the code-review defect chain =====
    //
    // Originally the strategies had no defense of their own: an undefined ApproverVerdict was
    // silently dropped by VerdictTally (AllOf resolved Approved with the bad decision erased,
    // Quorum mis-counted remaining votes, AnyOf could throw on tally.Resolve()!.Value). This is
    // the boundary check that stops an out-of-range value — the kind ASP.NET's default
    // JsonStringEnumConverter binds without complaint — from ever reaching them.

    [Fact]
    public void Validate_UndefinedVerdict_HasError()
    {
        var command = CreateValidCommand(approve: false) with { Verdict = (ApproverVerdict)42 };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Verdict");
    }

    [Fact]
    public void Validate_DefinedVerdictValues_NoVerdictError()
    {
        // Mutation control: only an undefined value should trip this rule, not the check itself
        // firing indiscriminately on every request that carries a Verdict.
        foreach (ApproverVerdict verdict in Enum.GetValues<ApproverVerdict>())
        {
            var approve = verdict == ApproverVerdict.Approve;
            var command = verdict == ApproverVerdict.Revise
                ? CreateValidCommand(verdict, approve, instructions: "use the other path")
                : CreateValidCommand(verdict, approve);

            var result = _validator.Validate(command);

            result.Errors.Should().NotContain(e => e.PropertyName == "Verdict");
        }
    }

    [Fact]
    public void Validate_NullVerdict_NoVerdictError()
    {
        // Mutation control: absence is not the same as an undefined value — a legacy caller
        // sending no Verdict at all must not be rejected by the defined-value rule.
        var result = _validator.Validate(CreateValidCommand(verdict: null));

        result.Errors.Should().NotContain(e => e.PropertyName == "Verdict");
    }

    // ===== Verdict / Approve contradiction =====

    [Fact]
    public void Validate_VerdictDenyWithApproveTrue_HasError()
    {
        var command = CreateValidCommand(verdict: ApproverVerdict.Deny, approve: true);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Verdict");
    }

    [Fact]
    public void Validate_VerdictReviseWithApproveTrue_HasError()
    {
        var command = CreateValidCommand(
            verdict: ApproverVerdict.Revise, approve: true, instructions: "use the other path");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Verdict");
    }

    // ===== Revise requires Instructions =====

    [Fact]
    public void Validate_ReviseWithBlankInstructions_HasError()
    {
        var command = CreateValidCommand(
            verdict: ApproverVerdict.Revise, approve: false, instructions: null);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Instructions");
    }

    [Fact]
    public void Validate_ReviseWithInstructions_NoError()
    {
        var command = CreateValidCommand(
            verdict: ApproverVerdict.Revise, approve: false, instructions: "use the other path");

        var result = _validator.Validate(command);

        result.Errors.Should().NotContain(e => e.PropertyName == "Instructions");
    }

    [Fact]
    public void Validate_DenyWithBlankInstructions_NoError()
    {
        // Mutation control: the "Instructions required" rule is scoped to Revise only.
        var command = CreateValidCommand(verdict: ApproverVerdict.Deny, approve: false, instructions: null);

        var result = _validator.Validate(command);

        result.Errors.Should().NotContain(e => e.PropertyName == "Instructions");
    }

    [Fact]
    public void Validate_InstructionsExceedsMaxLength_HasError()
    {
        var command = CreateValidCommand(
            verdict: ApproverVerdict.Revise,
            approve: false,
            instructions: new string('x', EscalationValidationRules.MaxInstructionsLength + 1));

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Instructions");
    }
}

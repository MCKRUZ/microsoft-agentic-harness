using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;
using EditOp = Domain.AI.SkillTraining.EditOp;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Validation rule tests for <see cref="SubmitChangeProposalCommandValidator"/>.</summary>
public sealed class SubmitChangeProposalCommandValidatorTests
{
    private readonly SubmitChangeProposalCommandValidator _validator = new();

    private static SubmitChangeProposalCommand Valid() => new()
    {
        Target = TestHelpers.DefaultTarget(),
        Diff = TestHelpers.DefaultDiff(),
        Summary = "rename foo to bar",
        BlastRadius = BlastRadius.Low
    };

    [Fact]
    public void Valid_PassesAllRules()
    {
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Target_Null_FailsValidation()
    {
        var cmd = Valid() with { Target = null! };
        _validator.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.Target);
    }

    [Fact]
    public void Diff_Empty_FailsValidation()
    {
        var cmd = Valid() with { Diff = [] };
        _validator.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.Diff);
    }

    [Fact]
    public void Diff_ExceedsMaxEdits_FailsValidation()
    {
        var tooMany = Enumerable.Range(0, SubmitChangeProposalCommandValidator.MaxEdits + 1)
            .Select(i => new ChangeEdit { Op = EditOp.Append, Content = "x" })
            .ToArray();
        var cmd = Valid() with { Diff = tooMany };

        _validator.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.Diff);
    }

    [Fact]
    public void Summary_Empty_FailsValidation()
    {
        var cmd = Valid() with { Summary = "" };
        _validator.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.Summary);
    }

    [Fact]
    public void Summary_TooLong_FailsValidation()
    {
        var cmd = Valid() with { Summary = new string('x', SubmitChangeProposalCommandValidator.MaxSummaryLength + 1) };
        _validator.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.Summary);
    }

    [Fact]
    public void RequiredGates_EmptyList_FailsValidation()
    {
        var cmd = Valid() with { RequiredGates = [] };
        _validator.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.RequiredGates);
    }

    [Fact]
    public void RequiredGates_NullElement_Tolerated_NullList_OK()
    {
        var cmd = Valid() with { RequiredGates = null };
        _validator.TestValidate(cmd).ShouldNotHaveValidationErrorFor(c => c.RequiredGates);
    }
}

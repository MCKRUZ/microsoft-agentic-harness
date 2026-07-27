using Application.Core.CQRS.Learnings;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Verifies <see cref="RecallLearningsQueryValidator"/> — the wire-level bounds for the
/// HTTP-facing learnings recall surface. These caps exist on the adapter query (not the shared
/// <see cref="RecallQueryValidator"/>) so the HTTP boundary is bounded without constraining the
/// internal agent-turn recall path; the boundary tests pin the exact limits from
/// <see cref="LearningsValidationRules"/>.
/// </summary>
public sealed class RecallLearningsQueryValidatorTests
{
    private readonly RecallLearningsQueryValidator _validator = new();

    [Fact]
    public void Validate_ValidInput_NoErrors()
    {
        var query = new RecallLearningsQuery { Context = "error handling conventions", MaxResults = 10 };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyContext_HasError()
    {
        var query = new RecallLearningsQuery { Context = "" };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.Context);
    }

    [Fact]
    public void Validate_ContextAtMaxLength_NoError()
    {
        var query = new RecallLearningsQuery
        {
            Context = new string('x', LearningsValidationRules.MaxContextLength)
        };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveValidationErrorFor(x => x.Context);
    }

    [Fact]
    public void Validate_ContextExceedsMaxLength_HasError()
    {
        var query = new RecallLearningsQuery
        {
            Context = new string('x', LearningsValidationRules.MaxContextLength + 1)
        };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.Context);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(LearningsValidationRules.MaxRecallResults + 1)]
    public void Validate_MaxResultsOutOfRange_HasError(int maxResults)
    {
        var query = new RecallLearningsQuery { Context = "valid", MaxResults = maxResults };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(LearningsValidationRules.MaxRecallResults)]
    public void Validate_MaxResultsAtBounds_NoError(int maxResults)
    {
        var query = new RecallLearningsQuery { Context = "valid", MaxResults = maxResults };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveValidationErrorFor(x => x.MaxResults);
    }
}

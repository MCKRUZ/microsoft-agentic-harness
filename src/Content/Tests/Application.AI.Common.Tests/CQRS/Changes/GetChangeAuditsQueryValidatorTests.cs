using Application.AI.Common.CQRS.Changes.GetChangeAudits;
using Domain.AI.Changes;
using Domain.Common.Constants;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Validator tests for <see cref="GetChangeAuditsQueryValidator"/>.</summary>
public sealed class GetChangeAuditsQueryValidatorTests
{
    private readonly GetChangeAuditsQueryValidator _validator = new();

    [Fact]
    public void Validate_Defaults_NoErrors()
    {
        var result = _validator.TestValidate(new GetChangeAuditsQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_StartAfterEnd_HasError()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _validator.TestValidate(new GetChangeAuditsQuery { Start = now, End = now.AddMinutes(-1) });
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void Validate_UndefinedDecision_HasError()
    {
        var result = _validator.TestValidate(new GetChangeAuditsQuery { Decision = (GateAction)999 });
        result.ShouldHaveValidationErrorFor(x => x.Decision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuditQueryDefaults.MaxResults + 1)]
    public void Validate_MaxResultsOutOfRange_HasError(int maxResults)
    {
        var result = _validator.TestValidate(new GetChangeAuditsQuery { MaxResults = maxResults });
        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }
}

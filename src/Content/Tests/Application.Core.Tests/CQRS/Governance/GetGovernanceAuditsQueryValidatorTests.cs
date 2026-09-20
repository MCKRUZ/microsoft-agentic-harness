using Application.Core.CQRS.Governance;
using Domain.Common.Constants;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Governance;

/// <summary>Validator tests for <see cref="GetGovernanceAuditsQueryValidator"/>.</summary>
public sealed class GetGovernanceAuditsQueryValidatorTests
{
    private readonly GetGovernanceAuditsQueryValidator _validator = new();

    [Fact]
    public void Validate_Defaults_NoErrors()
    {
        var result = _validator.TestValidate(new GetGovernanceAuditsQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuditQueryDefaults.MaxResults + 1)]
    public void Validate_MaxResultsOutOfRange_HasError(int maxResults)
    {
        var result = _validator.TestValidate(new GetGovernanceAuditsQuery { MaxResults = maxResults });
        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }

    [Fact]
    public void Validate_StartAfterEnd_HasError()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _validator.TestValidate(new GetGovernanceAuditsQuery
        {
            Start = now,
            End = now.AddDays(-1),
        });
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }
}

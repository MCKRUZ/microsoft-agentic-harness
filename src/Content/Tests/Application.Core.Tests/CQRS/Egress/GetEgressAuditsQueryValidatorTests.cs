using Application.Core.CQRS.Egress;
using Domain.Common.Constants;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Egress;

/// <summary>Validator tests for <see cref="GetEgressAuditsQueryValidator"/>.</summary>
public sealed class GetEgressAuditsQueryValidatorTests
{
    private readonly GetEgressAuditsQueryValidator _validator = new();

    [Fact]
    public void Validate_Defaults_NoErrors()
    {
        var result = _validator.TestValidate(new GetEgressAuditsQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuditQueryDefaults.MaxResults + 1)]
    public void Validate_MaxResultsOutOfRange_HasError(int maxResults)
    {
        var result = _validator.TestValidate(new GetEgressAuditsQuery { MaxResults = maxResults });
        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }

    [Fact]
    public void Validate_StartAfterEnd_HasError()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _validator.TestValidate(new GetEgressAuditsQuery
        {
            Start = now,
            End = now.AddDays(-1),
        });
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }
}

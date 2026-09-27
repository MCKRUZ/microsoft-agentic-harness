using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Domain.Common.Constants;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>Validator tests for <see cref="GenerateComplianceReportQueryValidator"/>.</summary>
public sealed class GenerateComplianceReportQueryValidatorTests
{
    private readonly GenerateComplianceReportQueryValidator _validator = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static GenerateComplianceReportQuery ValidQuery() => new()
    {
        CallerId = "operator-1",
        Start = Now.AddDays(-7),
        End = Now,
    };

    [Fact]
    public void Validate_ValidQuery_NoErrors()
    {
        var result = _validator.TestValidate(ValidQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_BlankCallerId_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with { CallerId = "" });
        result.ShouldHaveValidationErrorFor(x => x.CallerId);
    }

    [Fact]
    public void Validate_EndBeforeStart_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with { Start = Now, End = Now.AddDays(-1) });
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void Validate_WindowExceedsMaxDays_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with
        {
            Start = Now.AddDays(-(ComplianceReportDefaults.MaxWindowDays + 10)),
            End = Now,
        });
        result.ShouldHaveValidationErrorFor(x => x.End);
    }

    [Fact]
    public void Validate_WindowAtMaxDays_NoError()
    {
        var result = _validator.TestValidate(ValidQuery() with
        {
            Start = Now.AddDays(-ComplianceReportDefaults.MaxWindowDays),
            End = Now,
        });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuditQueryDefaults.MaxResults + 1)]
    public void Validate_MaxRecordsPerSourceOutOfRange_HasError(int maxRecords)
    {
        var result = _validator.TestValidate(ValidQuery() with { MaxRecordsPerSource = maxRecords });
        result.ShouldHaveValidationErrorFor(x => x.MaxRecordsPerSource);
    }
}

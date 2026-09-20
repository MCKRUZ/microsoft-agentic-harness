using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Constants;
using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Tests for <see cref="QueryEscalationAuditsQueryValidator"/> — mirrors
/// <c>DriftCommandValidationTests</c>' coverage of <c>GetDriftAuditsQueryValidator</c>: an
/// ordered window when both ends are supplied, a defined record-type filter, and a bounded
/// result cap.
/// </summary>
public sealed class QueryEscalationAuditsQueryValidatorTests
{
    private readonly QueryEscalationAuditsQueryValidator _validator = new();

    [Fact]
    public void Validate_Defaults_NoErrors()
    {
        var result = _validator.TestValidate(new QueryEscalationAuditsQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuditQueryDefaults.MaxResults + 1)]
    public void Validate_MaxResultsOutOfRange_HasError(int maxResults)
    {
        var result = _validator.TestValidate(new QueryEscalationAuditsQuery { MaxResults = maxResults });
        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }

    [Fact]
    public void Validate_StartAfterEnd_HasError()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _validator.TestValidate(new QueryEscalationAuditsQuery
        {
            Start = now,
            End = now.AddDays(-1)
        });
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void Validate_StartBeforeEnd_NoErrors()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _validator.TestValidate(new QueryEscalationAuditsQuery
        {
            Start = now.AddDays(-1),
            End = now
        });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_UndefinedRecordType_HasError()
    {
        var result = _validator.TestValidate(new QueryEscalationAuditsQuery
        {
            RecordType = (EscalationAuditRecordType)99
        });
        result.ShouldHaveValidationErrorFor(x => x.RecordType);
    }

    [Fact]
    public void Validate_DefinedRecordType_NoErrors()
    {
        var result = _validator.TestValidate(new QueryEscalationAuditsQuery
        {
            RecordType = EscalationAuditRecordType.Decision
        });
        result.ShouldNotHaveAnyValidationErrors();
    }
}

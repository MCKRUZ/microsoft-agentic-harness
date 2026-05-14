using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.AI.Common.Tests.Interfaces.DriftDetection;

public sealed class DriftDetectionDtoValidatorTests
{
    private readonly DriftEvaluationRequestValidator _evalValidator = new();
    private readonly DriftHistoryQueryValidator _historyValidator = new();
    private readonly DriftAuditQueryValidator _auditValidator = new();
    private readonly DriftBaselineUpdateRequestValidator _baselineValidator = new();

    [Fact]
    public void DriftEvaluationRequestValidator_EmptyDimensions_Fails()
    {
        var request = new DriftEvaluationRequest
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimensions = new Dictionary<DriftDimension, double>().AsReadOnly()
        };

        var result = _evalValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Dimensions);
    }

    [Fact]
    public void DriftEvaluationRequestValidator_EmptyScopeIdentifier_Fails()
    {
        var request = new DriftEvaluationRequest
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "",
            Dimensions = new Dictionary<DriftDimension, double>
            {
                [DriftDimension.Faithfulness] = 0.85
            }.AsReadOnly()
        };

        var result = _evalValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
    }

    [Fact]
    public void DriftEvaluationRequestValidator_ValidRequest_Passes()
    {
        var request = new DriftEvaluationRequest
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimensions = new Dictionary<DriftDimension, double>
            {
                [DriftDimension.Faithfulness] = 0.85
            }.AsReadOnly()
        };

        var result = _evalValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void DriftHistoryQueryValidator_StartAfterEnd_Fails()
    {
        var query = new DriftHistoryQuery
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddDays(-1)
        };

        var result = _historyValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void DriftHistoryQueryValidator_ValidRange_Passes()
    {
        var query = new DriftHistoryQuery
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Start = DateTimeOffset.UtcNow.AddDays(-7),
            End = DateTimeOffset.UtcNow
        };

        var result = _historyValidator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void DriftAuditQueryValidator_StartAfterEnd_Fails()
    {
        var query = new DriftAuditQuery
        {
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddDays(-1)
        };

        var result = _auditValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void DriftAuditQueryValidator_BothNull_Passes()
    {
        var query = new DriftAuditQuery();

        var result = _auditValidator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void DriftBaselineUpdateRequestValidator_EmptyScopeIdentifier_Fails()
    {
        var request = new DriftBaselineUpdateRequest
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = ""
        };

        var result = _baselineValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
    }

    [Fact]
    public void DriftBaselineUpdateRequestValidator_ValidRequest_Passes()
    {
        var request = new DriftBaselineUpdateRequest
        {
            Scope = DriftScope.Agent,
            ScopeIdentifier = "primary_agent"
        };

        var result = _baselineValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void DriftHistoryQueryValidator_EmptyScopeIdentifier_Fails()
    {
        var query = new DriftHistoryQuery
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "",
            Start = DateTimeOffset.UtcNow.AddDays(-7),
            End = DateTimeOffset.UtcNow
        };

        var result = _historyValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
    }

    [Fact]
    public void DriftHistoryQueryValidator_StartEqualsEnd_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        var query = new DriftHistoryQuery
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Start = now,
            End = now
        };

        var result = _historyValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void DriftAuditQueryValidator_OnlyStartProvided_Passes()
    {
        var query = new DriftAuditQuery
        {
            Start = DateTimeOffset.UtcNow.AddDays(-7)
        };

        var result = _auditValidator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void DriftAuditQueryValidator_OnlyEndProvided_Passes()
    {
        var query = new DriftAuditQuery
        {
            End = DateTimeOffset.UtcNow
        };

        var result = _auditValidator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }
}

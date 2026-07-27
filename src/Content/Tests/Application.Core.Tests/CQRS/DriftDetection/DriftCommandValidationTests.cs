using Application.Core.CQRS.DriftDetection;
using Domain.AI.DriftDetection;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.DriftDetection;

/// <summary>
/// Validator tests for the drift CQRS surface. The evaluation-push rules matter most: pushed
/// scores feed EWMA state and future baselines, so poison values (out-of-range, NaN, infinity,
/// undefined enum members) must die at the boundary.
/// </summary>
public sealed class DriftCommandValidationTests
{
    private readonly PushDriftEvaluationCommandValidator _pushValidator = new();
    private readonly RecalculateDriftBaselineCommandValidator _recalculateValidator = new();
    private readonly GetDriftBaselinesQueryValidator _baselinesValidator = new();
    private readonly GetDriftHistoryQueryValidator _historyValidator = new();
    private readonly GetDriftAuditsQueryValidator _auditsValidator = new();

    private static PushDriftEvaluationCommand CreatePushCommand(
        IReadOnlyDictionary<DriftDimension, double>? dimensions = null,
        DriftScope scope = DriftScope.Skill,
        string scopeIdentifier = "summarize",
        string callerId = "ops@contoso.com") => new()
    {
        Scope = scope,
        ScopeIdentifier = scopeIdentifier,
        Dimensions = dimensions ?? new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.85
        },
        CallerId = callerId
    };

    // == PushDriftEvaluationCommand ==

    [Fact]
    public void Validate_PushCommand_ValidInput_NoErrors()
    {
        var result = _pushValidator.TestValidate(CreatePushCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Validate_PushCommand_PoisonScore_HasError(double poison)
    {
        var command = CreatePushCommand(new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = poison
        });

        var result = _pushValidator.TestValidate(command);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Validate_PushCommand_BoundaryScore_NoErrors(double boundary)
    {
        var command = CreatePushCommand(new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = boundary
        });

        var result = _pushValidator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_PushCommand_EmptyDimensions_HasError()
    {
        var command = CreatePushCommand(new Dictionary<DriftDimension, double>());
        var result = _pushValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Dimensions);
    }

    [Fact]
    public void Validate_PushCommand_UndefinedDimensionKey_HasError()
    {
        var command = CreatePushCommand(new Dictionary<DriftDimension, double>
        {
            [(DriftDimension)999] = 0.5
        });

        var result = _pushValidator.TestValidate(command);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_PushCommand_TooManyDimensions_HasError()
    {
        var oversized = Enumerable.Range(0, DriftValidationRules.MaxDimensionsPerEvaluation + 1)
            .ToDictionary(i => (DriftDimension)i, _ => 0.5);
        var command = CreatePushCommand(oversized);

        var result = _pushValidator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Dimensions);
    }

    [Fact]
    public void Validate_PushCommand_UndefinedScope_HasError()
    {
        var result = _pushValidator.TestValidate(CreatePushCommand(scope: (DriftScope)42));
        result.ShouldHaveValidationErrorFor(x => x.Scope);
    }

    [Fact]
    public void Validate_PushCommand_EmptyScopeIdentifier_HasError()
    {
        var result = _pushValidator.TestValidate(CreatePushCommand(scopeIdentifier: ""));
        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
    }

    [Fact]
    public void Validate_PushCommand_OversizedScopeIdentifier_HasError()
    {
        var result = _pushValidator.TestValidate(CreatePushCommand(
            scopeIdentifier: new string('x', DriftValidationRules.MaxScopeIdentifierLength + 1)));
        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
    }

    [Fact]
    public void Validate_PushCommand_EmptyCallerId_HasError()
    {
        var result = _pushValidator.TestValidate(CreatePushCommand(callerId: ""));
        result.ShouldHaveValidationErrorFor(x => x.CallerId);
    }

    // == RecalculateDriftBaselineCommand ==

    [Fact]
    public void Validate_RecalculateCommand_ValidInput_NoErrors()
    {
        var result = _recalculateValidator.TestValidate(new RecalculateDriftBaselineCommand
        {
            BaselineId = Guid.NewGuid(),
            CallerId = "ops@contoso.com"
        });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_RecalculateCommand_EmptyBaselineId_HasError()
    {
        var result = _recalculateValidator.TestValidate(new RecalculateDriftBaselineCommand
        {
            BaselineId = Guid.Empty,
            CallerId = "ops@contoso.com"
        });
        result.ShouldHaveValidationErrorFor(x => x.BaselineId);
    }

    [Fact]
    public void Validate_RecalculateCommand_EmptyCallerId_HasError()
    {
        var result = _recalculateValidator.TestValidate(new RecalculateDriftBaselineCommand
        {
            BaselineId = Guid.NewGuid(),
            CallerId = ""
        });
        result.ShouldHaveValidationErrorFor(x => x.CallerId);
    }

    // == GetDriftBaselinesQuery ==

    [Fact]
    public void Validate_BaselinesQuery_NullScope_NoErrors()
    {
        var result = _baselinesValidator.TestValidate(new GetDriftBaselinesQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_BaselinesQuery_UndefinedScope_HasError()
    {
        var result = _baselinesValidator.TestValidate(new GetDriftBaselinesQuery { Scope = (DriftScope)42 });
        result.ShouldHaveValidationErrorFor(x => x.Scope);
    }

    // == GetDriftHistoryQuery ==

    private static GetDriftHistoryQuery CreateHistoryQuery(
        DateTimeOffset? start = null, DateTimeOffset? end = null, string scopeIdentifier = "summarize") => new()
    {
        Scope = DriftScope.Skill,
        ScopeIdentifier = scopeIdentifier,
        Start = start ?? new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        End = end ?? new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public void Validate_HistoryQuery_ValidInput_NoErrors()
    {
        var result = _historyValidator.TestValidate(CreateHistoryQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_HistoryQuery_StartNotBeforeEnd_HasError()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _historyValidator.TestValidate(CreateHistoryQuery(start: now, end: now));
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void Validate_HistoryQuery_WindowOverCap_HasError()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = _historyValidator.TestValidate(CreateHistoryQuery(
            start: start,
            end: start.AddDays(DriftValidationRules.MaxHistoryWindowDays + 1)));
        result.ShouldHaveValidationErrorFor(x => x.End);
    }

    [Fact]
    public void Validate_HistoryQuery_WindowAtCap_NoErrors()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = _historyValidator.TestValidate(CreateHistoryQuery(
            start: start,
            end: start.AddDays(DriftValidationRules.MaxHistoryWindowDays)));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_HistoryQuery_EmptyScopeIdentifier_HasError()
    {
        var result = _historyValidator.TestValidate(CreateHistoryQuery(scopeIdentifier: ""));
        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
    }

    // == GetDriftAuditsQuery ==

    [Fact]
    public void Validate_AuditsQuery_Defaults_NoErrors()
    {
        var result = _auditsValidator.TestValidate(new GetDriftAuditsQuery());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DriftValidationRules.MaxAuditResults + 1)]
    public void Validate_AuditsQuery_MaxResultsOutOfRange_HasError(int maxResults)
    {
        var result = _auditsValidator.TestValidate(new GetDriftAuditsQuery { MaxResults = maxResults });
        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }

    [Fact]
    public void Validate_AuditsQuery_StartAfterEnd_HasError()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _auditsValidator.TestValidate(new GetDriftAuditsQuery
        {
            Start = now,
            End = now.AddDays(-1)
        });
        result.ShouldHaveValidationErrorFor(x => x.Start);
    }

    [Fact]
    public void Validate_AuditsQuery_UndefinedRecordType_HasError()
    {
        var result = _auditsValidator.TestValidate(new GetDriftAuditsQuery
        {
            RecordType = (DriftAuditRecordType)99
        });
        result.ShouldHaveValidationErrorFor(x => x.RecordType);
    }
}

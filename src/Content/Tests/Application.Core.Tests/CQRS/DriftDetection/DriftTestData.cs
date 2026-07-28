using Domain.AI.DriftDetection;

namespace Application.Core.Tests.CQRS.DriftDetection;

/// <summary>Shared builders for drift CQRS tests.</summary>
internal static class DriftTestData
{
    public static DriftScore CreateScore(
        DriftScope scope = DriftScope.Skill,
        string scopeIdentifier = "summarize",
        DriftSeverity severity = DriftSeverity.None) => new()
    {
        ScoreId = Guid.NewGuid(),
        BaselineId = Guid.NewGuid(),
        Scope = scope,
        ScopeIdentifier = scopeIdentifier,
        Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>
        {
            [DriftDimension.Faithfulness] = new()
            {
                CurrentValue = 0.85,
                BaselineValue = 0.85,
                EwmaValue = 0.85,
                Deviation = 0.1
            }
        },
        OverallDrift = 0.1,
        Severity = severity,
        ScoredAt = DateTimeOffset.UtcNow
    };

    public static DriftBaseline CreateBaseline(
        Guid? baselineId = null,
        DriftScope scope = DriftScope.Skill,
        string scopeIdentifier = "summarize") => new()
    {
        BaselineId = baselineId ?? Guid.NewGuid(),
        Scope = scope,
        ScopeIdentifier = scopeIdentifier,
        Dimensions = new Dictionary<DriftDimension, double> { [DriftDimension.Faithfulness] = 0.85 },
        DimensionSigmas = new Dictionary<DriftDimension, double> { [DriftDimension.Faithfulness] = 0.02 },
        SampleCount = 20,
        WindowStart = DateTimeOffset.UtcNow.AddDays(-7),
        WindowEnd = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };

    public static DriftAuditRecord CreateAuditRecord(DateTimeOffset recordedAt) => new()
    {
        RecordId = Guid.NewGuid(),
        EventId = Guid.NewGuid(),
        RecordType = DriftAuditRecordType.Detected,
        Payload = "{}",
        RecordedAt = recordedAt
    };
}

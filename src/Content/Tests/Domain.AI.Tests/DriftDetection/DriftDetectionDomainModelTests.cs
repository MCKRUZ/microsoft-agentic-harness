using System.Text.Json;
using Domain.AI.DriftDetection;
using Xunit;

namespace Domain.AI.Tests.DriftDetection;

/// <summary>
/// Tests for drift detection domain records, enums, and their properties.
/// </summary>
public sealed class DriftDetectionDomainModelTests
{
    // --- DriftDimension enum ---

    [Fact]
    public void DriftDimension_HasExactlySixMembers()
    {
        var values = Enum.GetValues<DriftDimension>();
        Assert.Equal(6, values.Length);
    }

    [Theory]
    [InlineData(nameof(DriftDimension.Faithfulness))]
    [InlineData(nameof(DriftDimension.Relevance))]
    [InlineData(nameof(DriftDimension.StructuralConformance))]
    [InlineData(nameof(DriftDimension.ToolUsageAccuracy))]
    [InlineData(nameof(DriftDimension.Coherence))]
    [InlineData(nameof(DriftDimension.InstructionFollowing))]
    public void DriftDimension_ContainsExpectedMember(string memberName)
    {
        Assert.True(Enum.TryParse<DriftDimension>(memberName, out _));
    }

    // --- DriftSeverity enum ---

    [Fact]
    public void DriftSeverity_IntegerCasting_MaintainsOrdering()
    {
        Assert.Equal(0, (int)DriftSeverity.None);
        Assert.Equal(1, (int)DriftSeverity.Warn);
        Assert.Equal(2, (int)DriftSeverity.Alert);
        Assert.Equal(3, (int)DriftSeverity.Escalate);
        Assert.True(DriftSeverity.None < DriftSeverity.Warn);
        Assert.True(DriftSeverity.Warn < DriftSeverity.Alert);
        Assert.True(DriftSeverity.Alert < DriftSeverity.Escalate);
    }

    // --- DriftScope enum ---

    [Theory]
    [InlineData(nameof(DriftScope.Agent))]
    [InlineData(nameof(DriftScope.Skill))]
    [InlineData(nameof(DriftScope.TaskType))]
    public void DriftScope_ContainsExpectedMember(string memberName)
    {
        Assert.True(Enum.TryParse<DriftScope>(memberName, out _));
    }

    // --- DriftDimensionScore ---

    [Fact]
    public void DriftDimensionScore_Construction_RoundTripsAllProperties()
    {
        var score = new DriftDimensionScore
        {
            CurrentValue = 0.7,
            BaselineValue = 0.85,
            EwmaValue = 0.78,
            Deviation = 1.5
        };

        Assert.Equal(0.7, score.CurrentValue);
        Assert.Equal(0.85, score.BaselineValue);
        Assert.Equal(0.78, score.EwmaValue);
        Assert.Equal(1.5, score.Deviation);
    }

    [Fact]
    public void DriftDimensionScore_Deviation_StoresValueFromScorer()
    {
        var score = new DriftDimensionScore
        {
            CurrentValue = 0.5,
            BaselineValue = 0.9,
            EwmaValue = 0.6,
            Deviation = 3.2
        };

        Assert.Equal(3.2, score.Deviation);
    }

    // --- DriftBaseline ---

    [Fact]
    public void DriftBaseline_Construction_SetsAllProperties()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var dimensions = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.9,
            [DriftDimension.Relevance] = 0.85
        };
        var sigmas = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.05,
            [DriftDimension.Relevance] = 0.07
        };

        var baseline = new DriftBaseline
        {
            BaselineId = id,
            Scope = DriftScope.Agent,
            ScopeIdentifier = "agent-1",
            Dimensions = dimensions.AsReadOnly(),
            DimensionSigmas = sigmas.AsReadOnly(),
            SampleCount = 50,
            WindowStart = now.AddDays(-7),
            WindowEnd = now,
            CreatedAt = now
        };

        Assert.Equal(id, baseline.BaselineId);
        Assert.Equal(DriftScope.Agent, baseline.Scope);
        Assert.Equal("agent-1", baseline.ScopeIdentifier);
        Assert.Equal(2, baseline.Dimensions.Count);
        Assert.Equal(0.9, baseline.Dimensions[DriftDimension.Faithfulness]);
        Assert.Equal(50, baseline.SampleCount);
    }

    [Fact]
    public void DriftBaseline_Dimensions_AreIReadOnlyDictionary()
    {
        var baseline = new DriftBaseline
        {
            BaselineId = Guid.NewGuid(),
            Scope = DriftScope.Skill,
            ScopeIdentifier = "summarize",
            Dimensions = new Dictionary<DriftDimension, double>().AsReadOnly(),
            DimensionSigmas = new Dictionary<DriftDimension, double>().AsReadOnly(),
            SampleCount = 10,
            WindowStart = DateTimeOffset.UtcNow.AddDays(-1),
            WindowEnd = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.IsAssignableFrom<IReadOnlyDictionary<DriftDimension, double>>(baseline.Dimensions);
        Assert.IsAssignableFrom<IReadOnlyDictionary<DriftDimension, double>>(baseline.DimensionSigmas);
    }

    // --- DriftScore ---

    [Fact]
    public void DriftScore_Severity_RoundTripsExpectedValue()
    {
        var score = CreateDriftScore(DriftSeverity.Alert, 2.5);
        Assert.Equal(DriftSeverity.Alert, score.Severity);
    }

    [Fact]
    public void DriftScore_OverallDrift_StoresMaxDeviation()
    {
        var score = CreateDriftScore(DriftSeverity.Warn, 1.8);
        Assert.Equal(1.8, score.OverallDrift);
    }

    // --- DriftEvent ---

    [Fact]
    public void DriftEvent_NullResolution_RepresentsUnresolvedDrift()
    {
        var driftScore = CreateDriftScore(DriftSeverity.Alert, 2.0);
        var evt = new DriftEvent
        {
            EventId = Guid.NewGuid(),
            DriftScore = driftScore,
            Resolution = null,
            DetectedAt = DateTimeOffset.UtcNow
        };

        Assert.Null(evt.Resolution);
        Assert.NotEqual(Guid.Empty, evt.EventId);
        Assert.Equal(driftScore, evt.DriftScore);
    }

    [Fact]
    public void DriftEvent_WithResolution_RepresentsResolvedDrift()
    {
        var driftScore = CreateDriftScore(DriftSeverity.Warn, 1.5);
        var resolution = new DriftResolution
        {
            ResolvedBy = DriftResolutionType.LearningApplied,
            ResolutionId = "learning-42",
            ResolvedAt = DateTimeOffset.UtcNow
        };

        var evt = new DriftEvent
        {
            EventId = Guid.NewGuid(),
            DriftScore = driftScore,
            Resolution = resolution,
            DetectedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        Assert.NotNull(evt.Resolution);
        Assert.Equal(DriftResolutionType.LearningApplied, evt.Resolution.ResolvedBy);
    }

    // --- DriftResolution ---

    [Theory]
    [InlineData(nameof(DriftResolutionType.LearningApplied))]
    [InlineData(nameof(DriftResolutionType.BaselineAdjusted))]
    [InlineData(nameof(DriftResolutionType.ManualDismissal))]
    [InlineData(nameof(DriftResolutionType.EscalationResolved))]
    public void DriftResolutionType_ContainsExpectedMember(string memberName)
    {
        Assert.True(Enum.TryParse<DriftResolutionType>(memberName, out _));
    }

    [Theory]
    [InlineData(DriftResolutionType.LearningApplied)]
    [InlineData(DriftResolutionType.BaselineAdjusted)]
    [InlineData(DriftResolutionType.ManualDismissal)]
    [InlineData(DriftResolutionType.EscalationResolved)]
    public void DriftResolution_Construction_WithEachType(DriftResolutionType type)
    {
        var resolution = new DriftResolution
        {
            ResolvedBy = type,
            ResolutionId = $"ref-{type}",
            ResolvedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(type, resolution.ResolvedBy);
        Assert.Equal($"ref-{type}", resolution.ResolutionId);
    }

    // --- DriftAuditRecord ---

    [Theory]
    [InlineData(nameof(DriftAuditRecordType.Detected))]
    [InlineData(nameof(DriftAuditRecordType.Resolved))]
    [InlineData(nameof(DriftAuditRecordType.BaselineUpdated))]
    [InlineData(nameof(DriftAuditRecordType.EscalationTriggered))]
    public void DriftAuditRecordType_ContainsExpectedMember(string memberName)
    {
        Assert.True(Enum.TryParse<DriftAuditRecordType>(memberName, out _));
    }

    [Fact]
    public void DriftAuditRecord_Construction_WithJsonPayload()
    {
        var eventId = Guid.NewGuid();
        var data = JsonSerializer.Serialize(new { dimension = "Faithfulness", deviation = 2.1 });

        var record = new DriftAuditRecord
        {
            RecordId = Guid.NewGuid(),
            EventId = eventId,
            RecordType = DriftAuditRecordType.Detected,
            Payload = data,
            RecordedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(eventId, record.EventId);
        Assert.Equal(DriftAuditRecordType.Detected, record.RecordType);
        Assert.Contains("Faithfulness", record.Payload);
    }

    [Fact]
    public void DriftAuditRecord_JsonRoundTrip_PreservesEquality()
    {
        var record = new DriftAuditRecord
        {
            RecordId = Guid.NewGuid(),
            EventId = Guid.NewGuid(),
            RecordType = DriftAuditRecordType.Resolved,
            Payload = """{"resolution":"baseline_adjusted"}""",
            RecordedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(record);
        var deserialized = JsonSerializer.Deserialize<DriftAuditRecord>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(record.RecordId, deserialized.RecordId);
        Assert.Equal(record.EventId, deserialized.EventId);
        Assert.Equal(record.RecordType, deserialized.RecordType);
        Assert.Equal(record.Payload, deserialized.Payload);
        Assert.Equal(record.RecordedAt, deserialized.RecordedAt);
    }

    // --- Helpers ---

    private static DriftScore CreateDriftScore(DriftSeverity severity, double overallDrift)
    {
        return new DriftScore
        {
            ScoreId = Guid.NewGuid(),
            BaselineId = Guid.NewGuid(),
            Scope = DriftScope.Agent,
            ScopeIdentifier = "test-agent",
            Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>
            {
                [DriftDimension.Faithfulness] = new DriftDimensionScore
                {
                    CurrentValue = 0.7,
                    BaselineValue = 0.9,
                    EwmaValue = 0.75,
                    Deviation = overallDrift
                }
            }.AsReadOnly(),
            OverallDrift = overallDrift,
            Severity = severity,
            ScoredAt = DateTimeOffset.UtcNow
        };
    }
}

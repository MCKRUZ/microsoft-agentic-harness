using Domain.AI.Learnings;
using Xunit;

namespace Domain.AI.Tests.Learnings;

/// <summary>
/// Tests for learnings domain records and enums.
/// </summary>
public sealed class LearningsDomainModelTests
{
    // --- LearningCategory enum ---

    [Theory]
    [InlineData(nameof(LearningCategory.FactualCorrection))]
    [InlineData(nameof(LearningCategory.StylePreference))]
    [InlineData(nameof(LearningCategory.ToolUsagePattern))]
    [InlineData(nameof(LearningCategory.DomainKnowledge))]
    [InlineData(nameof(LearningCategory.InstructionUpdate))]
    public void LearningCategory_ContainsExpectedMember(string memberName)
    {
        Assert.True(Enum.TryParse<LearningCategory>(memberName, out _));
    }

    [Fact]
    public void LearningCategory_HasExactlyFiveMembers()
    {
        Assert.Equal(5, Enum.GetValues<LearningCategory>().Length);
    }

    // --- DecayClass enum ---

    [Fact]
    public void DecayClass_HasExactlyThreeMembers()
    {
        Assert.Equal(3, Enum.GetValues<DecayClass>().Length);
    }

    [Fact]
    public void DecayClass_IntegerValues_MatchExpectedOrdering()
    {
        Assert.Equal(0, (int)DecayClass.Volatile);
        Assert.Equal(1, (int)DecayClass.Stable);
        Assert.Equal(2, (int)DecayClass.Permanent);
    }

    // --- LearningSourceType enum ---

    [Fact]
    public void LearningSourceType_HasExactlyFiveMembers()
    {
        Assert.Equal(5, Enum.GetValues<LearningSourceType>().Length);
    }

    [Theory]
    [InlineData(nameof(LearningSourceType.HumanCorrection))]
    [InlineData(nameof(LearningSourceType.DriftDetection))]
    [InlineData(nameof(LearningSourceType.EscalationResolution))]
    [InlineData(nameof(LearningSourceType.AgentSelfImprovement))]
    [InlineData(nameof(LearningSourceType.ManualEntry))]
    public void LearningSourceType_ContainsExpectedMember(string memberName)
    {
        Assert.True(Enum.TryParse<LearningSourceType>(memberName, out _));
    }

    // --- LearningScope ---

    [Fact]
    public void LearningScope_WithOnlyAgentId_HasAgentIdSet()
    {
        var scope = new LearningScope { AgentId = "agent-1" };

        Assert.Equal("agent-1", scope.AgentId);
        Assert.Null(scope.TeamId);
        Assert.False(scope.IsGlobal);
    }

    [Fact]
    public void LearningScope_WithOnlyTeamId_HasTeamIdSet()
    {
        var scope = new LearningScope { TeamId = "team-1" };

        Assert.Null(scope.AgentId);
        Assert.Equal("team-1", scope.TeamId);
        Assert.False(scope.IsGlobal);
    }

    [Fact]
    public void LearningScope_WithIsGlobalTrue_AndNoAgentOrTeam()
    {
        var scope = new LearningScope { IsGlobal = true };

        Assert.Null(scope.AgentId);
        Assert.Null(scope.TeamId);
        Assert.True(scope.IsGlobal);
    }

    [Fact]
    public void LearningScope_WithAllThreeSet_AllAccessible()
    {
        var scope = new LearningScope { AgentId = "a", TeamId = "t", IsGlobal = true };

        Assert.Equal("a", scope.AgentId);
        Assert.Equal("t", scope.TeamId);
        Assert.True(scope.IsGlobal);
    }

    // --- LearningEntry ---

    [Fact]
    public void LearningEntry_WithAllFields_RoundTrips()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var entry = new LearningEntry
        {
            LearningId = id,
            Category = LearningCategory.FactualCorrection,
            DecayClass = DecayClass.Permanent,
            Scope = new LearningScope { AgentId = "agent-1" },
            Content = "The API rate limit is 100 req/min, not 1000",
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-42",
                SourceDescription = "User corrected rate limit info"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "human_review",
                OriginTask = "correction_capture",
                OriginTimestamp = now,
                Confidence = 0.95
            },
            FeedbackWeight = 2.5,
            UpdateCount = 3,
            CreatedAt = now,
            LastAccessedAt = now.AddMinutes(10),
            LastReinforcedAt = now.AddMinutes(5),
            IsDeleted = false,
            DeleteReason = null
        };

        Assert.Equal(id, entry.LearningId);
        Assert.Equal(LearningCategory.FactualCorrection, entry.Category);
        Assert.Equal(DecayClass.Permanent, entry.DecayClass);
        Assert.Equal("agent-1", entry.Scope.AgentId);
        Assert.Contains("rate limit", entry.Content);
        Assert.Equal(LearningSourceType.HumanCorrection, entry.Source.SourceType);
        Assert.Equal(0.95, entry.Provenance.Confidence);
        Assert.Equal(2.5, entry.FeedbackWeight);
        Assert.Equal(3, entry.UpdateCount);
        Assert.NotNull(entry.LastAccessedAt);
        Assert.NotNull(entry.LastReinforcedAt);
    }

    [Fact]
    public void LearningEntry_DefaultFeedbackWeight_IsOne()
    {
        var entry = CreateMinimalEntry();

        Assert.Equal(1.0, entry.FeedbackWeight);
        Assert.Equal(0, entry.UpdateCount);
    }

    [Fact]
    public void LearningEntry_DefaultOptionalFields_AreNull()
    {
        var entry = CreateMinimalEntry();

        Assert.Null(entry.LastAccessedAt);
        Assert.Null(entry.LastReinforcedAt);
        Assert.False(entry.IsDeleted);
        Assert.Null(entry.DeleteReason);
    }

    // --- WeightedLearning ---

    [Fact]
    public void WeightedLearning_FinalScore_StoresPreComputedValue()
    {
        var entry = CreateMinimalEntry();
        var weighted = new WeightedLearning
        {
            Learning = entry,
            RelevanceScore = 0.9,
            FeedbackScore = 1.5,
            FreshnessScore = 0.8,
            FinalScore = 0.87
        };

        Assert.Equal(0.87, weighted.FinalScore);
        Assert.Equal(0.9, weighted.RelevanceScore);
        Assert.Equal(1.5, weighted.FeedbackScore);
        Assert.Equal(0.8, weighted.FreshnessScore);
        Assert.Equal(entry, weighted.Learning);
    }

    // --- LearningSource ---

    [Theory]
    [InlineData(LearningSourceType.HumanCorrection)]
    [InlineData(LearningSourceType.DriftDetection)]
    [InlineData(LearningSourceType.EscalationResolution)]
    [InlineData(LearningSourceType.AgentSelfImprovement)]
    [InlineData(LearningSourceType.ManualEntry)]
    public void LearningSource_ConstructionForEachSourceType(LearningSourceType sourceType)
    {
        var source = new LearningSource
        {
            SourceType = sourceType,
            SourceId = $"ref-{sourceType}",
            SourceDescription = $"Created by {sourceType}"
        };

        Assert.Equal(sourceType, source.SourceType);
        Assert.Equal($"ref-{sourceType}", source.SourceId);
    }

    // --- LearningProvenance ---

    [Fact]
    public void LearningProvenance_WithConfidence_StoresRawValue()
    {
        var prov1 = new LearningProvenance
        {
            OriginPipeline = "drift_correction",
            OriginTask = "auto_correct",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.85
        };

        var prov2 = new LearningProvenance
        {
            OriginPipeline = "test",
            OriginTask = "test",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.0
        };

        Assert.Equal(0.85, prov1.Confidence);
        Assert.Equal(0.0, prov2.Confidence);
    }

    // --- Helpers ---

    private static LearningEntry CreateMinimalEntry()
    {
        return new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = LearningCategory.DomainKnowledge,
            DecayClass = DecayClass.Stable,
            Scope = new LearningScope { AgentId = "test-agent" },
            Content = "Test learning content",
            Source = new LearningSource
            {
                SourceType = LearningSourceType.ManualEntry,
                SourceId = "test",
                SourceDescription = "Test source"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "test",
                OriginTask = "test",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 1.0
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

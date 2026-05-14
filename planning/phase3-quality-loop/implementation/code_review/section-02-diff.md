diff --git a/src/Content/Domain/Domain.AI/Learnings/DecayClass.cs b/src/Content/Domain/Domain.AI/Learnings/DecayClass.cs
new file mode 100644
index 0000000..717b6e2
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/DecayClass.cs
@@ -0,0 +1,18 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// Determines the temporal decay rate for a learning entry.
+/// <see cref="Volatile"/> entries expire quickly (default 7 days),
+/// <see cref="Stable"/> entries persist longer (default 180 days),
+/// and <see cref="Permanent"/> entries never decay.
+/// Shelf lives are configurable via <c>LearningsConfig</c>.
+/// </summary>
+public enum DecayClass
+{
+    /// <summary>Short-lived knowledge. Decays linearly over VolatileShelfLifeDays.</summary>
+    Volatile = 0,
+    /// <summary>Long-lived knowledge. Decays linearly over StableShelfLifeDays.</summary>
+    Stable = 1,
+    /// <summary>Immortal knowledge. Freshness always returns 1.0.</summary>
+    Permanent = 2
+}
diff --git a/src/Content/Domain/Domain.AI/Learnings/LearningCategory.cs b/src/Content/Domain/Domain.AI/Learnings/LearningCategory.cs
new file mode 100644
index 0000000..459f225
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/LearningCategory.cs
@@ -0,0 +1,21 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// Classifies the type of knowledge a learning entry represents.
+/// The category drives default <see cref="DecayClass"/> assignment:
+/// <see cref="FactualCorrection"/> -> Permanent,
+/// <see cref="StylePreference"/> -> Stable, etc.
+/// </summary>
+public enum LearningCategory
+{
+    /// <summary>A correction to factual output (wrong date, name, API signature).</summary>
+    FactualCorrection,
+    /// <summary>A user preference for tone, format, or style.</summary>
+    StylePreference,
+    /// <summary>A pattern about when/how to use a specific tool.</summary>
+    ToolUsagePattern,
+    /// <summary>Domain-specific knowledge not in training data.</summary>
+    DomainKnowledge,
+    /// <summary>An update to standing instructions or behavioral rules.</summary>
+    InstructionUpdate
+}
diff --git a/src/Content/Domain/Domain.AI/Learnings/LearningEntry.cs b/src/Content/Domain/Domain.AI/Learnings/LearningEntry.cs
new file mode 100644
index 0000000..ebaf5fa
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/LearningEntry.cs
@@ -0,0 +1,66 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// The core learning record, representing a piece of knowledge captured from corrections,
+/// drift events, escalation resolutions, or manual entries. Persisted as a graph node by
+/// <c>GraphLearningsStore</c> with deterministic ID <c>"learning:{LearningId}"</c>.
+/// </summary>
+/// <remarks>
+/// <see cref="FeedbackWeight"/> is updated via exponential moving average in
+/// <c>ImproveLearningCommandHandler</c>. Higher weights indicate learnings that have been
+/// repeatedly validated as useful. The weight influences recall ranking via the formula:
+/// <c>finalScore = (1 - alpha) * relevance + alpha * min(feedback * freshness, ceiling)</c>.
+/// <see cref="DecayClass"/> determines temporal decay behavior. <see cref="LastReinforcedAt"/>
+/// resets the decay clock when a learning receives positive feedback.
+/// </remarks>
+public sealed record LearningEntry
+{
+    /// <summary>Unique identifier for this learning.</summary>
+    public required Guid LearningId { get; init; }
+
+    /// <summary>What kind of knowledge this learning represents.</summary>
+    public required LearningCategory Category { get; init; }
+
+    /// <summary>How quickly this learning decays over time.</summary>
+    public required DecayClass DecayClass { get; init; }
+
+    /// <summary>Visibility scope (agent, team, or global).</summary>
+    public required LearningScope Scope { get; init; }
+
+    /// <summary>The actual knowledge content -- a natural language description of what was learned.</summary>
+    public required string Content { get; init; }
+
+    /// <summary>What produced this learning.</summary>
+    public required LearningSource Source { get; init; }
+
+    /// <summary>Pipeline provenance metadata.</summary>
+    public required LearningProvenance Provenance { get; init; }
+
+    /// <summary>
+    /// EMA-weighted feedback score. Default 1.0 (neutral). Updated by
+    /// <c>ImproveLearningCommandHandler</c>. Range: 0.0+ (no upper bound enforced at
+    /// domain level; ceiling applied during recall scoring).
+    /// </summary>
+    public double FeedbackWeight { get; init; } = 1.0;
+
+    /// <summary>Number of times this learning's feedback weight has been updated.</summary>
+    public int UpdateCount { get; init; }
+
+    /// <summary>When this learning was first created.</summary>
+    public required DateTimeOffset CreatedAt { get; init; }
+
+    /// <summary>When this learning was last accessed during a recall query. Null if never recalled.</summary>
+    public DateTimeOffset? LastAccessedAt { get; init; }
+
+    /// <summary>
+    /// When this learning was last reinforced via positive feedback. Null if never reinforced.
+    /// Used by <c>DefaultLearningDecayService</c> to reset the decay clock.
+    /// </summary>
+    public DateTimeOffset? LastReinforcedAt { get; init; }
+
+    /// <summary>Soft-delete flag. Deleted learnings remain in the graph for audit but are excluded from search.</summary>
+    public bool IsDeleted { get; init; }
+
+    /// <summary>Reason for soft-deletion. Null when not deleted.</summary>
+    public string? DeleteReason { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Learnings/LearningProvenance.cs b/src/Content/Domain/Domain.AI/Learnings/LearningProvenance.cs
new file mode 100644
index 0000000..37bdd46
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/LearningProvenance.cs
@@ -0,0 +1,23 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// Detailed provenance metadata for a learning entry, tracking which pipeline and task
+/// produced the knowledge and with what confidence.
+/// </summary>
+public sealed record LearningProvenance
+{
+    /// <summary>The pipeline that produced this learning (e.g., "escalation_resolution", "drift_correction").</summary>
+    public required string OriginPipeline { get; init; }
+
+    /// <summary>The specific task within the pipeline (e.g., "human_review", "auto_correct").</summary>
+    public required string OriginTask { get; init; }
+
+    /// <summary>When the originating event occurred.</summary>
+    public required DateTimeOffset OriginTimestamp { get; init; }
+
+    /// <summary>
+    /// Confidence in the learning's correctness, normalized to 0.0-1.0.
+    /// Validated by <c>RememberCommandValidator</c> to enforce the [0, 1] range.
+    /// </summary>
+    public required double Confidence { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Learnings/LearningScope.cs b/src/Content/Domain/Domain.AI/Learnings/LearningScope.cs
new file mode 100644
index 0000000..8b38f27
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/LearningScope.cs
@@ -0,0 +1,26 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// Defines the visibility scope for a learning entry using a 3-tier hierarchy:
+/// agent-specific -> team-wide -> global. A learning scoped to agent "X" in team "T"
+/// is visible only to agent X. A team-scoped learning is visible to all agents in
+/// team T. A global learning is visible to all agents.
+/// </summary>
+/// <remarks>
+/// Scope resolution during recall: if querying for agent "X" in team "T", the store
+/// returns learnings scoped to X, learnings scoped to T, and global learnings --
+/// merging all levels with deduplication by <see cref="LearningEntry.LearningId"/>.
+/// At least one of <see cref="AgentId"/>, <see cref="TeamId"/>, or <see cref="IsGlobal"/>
+/// must be set. Validation is enforced by <c>RememberCommandValidator</c> (section 06).
+/// </remarks>
+public sealed record LearningScope
+{
+    /// <summary>Scopes the learning to a specific agent. Null means not agent-scoped.</summary>
+    public string? AgentId { get; init; }
+
+    /// <summary>Scopes the learning to a team of agents. Null means not team-scoped.</summary>
+    public string? TeamId { get; init; }
+
+    /// <summary>When true, the learning is visible to all agents regardless of team.</summary>
+    public bool IsGlobal { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Learnings/LearningSource.cs b/src/Content/Domain/Domain.AI/Learnings/LearningSource.cs
new file mode 100644
index 0000000..ab32dcc
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/LearningSource.cs
@@ -0,0 +1,21 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// Identifies what created a learning entry -- a human correction, drift event,
+/// escalation resolution, or agent self-improvement. The <see cref="SourceId"/>
+/// correlates back to the originating entity (e.g., escalation ID, drift event ID).
+/// </summary>
+public sealed record LearningSource
+{
+    /// <summary>The origin type that produced this learning.</summary>
+    public required LearningSourceType SourceType { get; init; }
+
+    /// <summary>
+    /// Identifier of the originating entity (escalation ID, drift event ID, user session ID).
+    /// Used for audit trail correlation.
+    /// </summary>
+    public required string SourceId { get; init; }
+
+    /// <summary>Human-readable description of how this learning was created.</summary>
+    public required string SourceDescription { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/Learnings/LearningSourceType.cs b/src/Content/Domain/Domain.AI/Learnings/LearningSourceType.cs
new file mode 100644
index 0000000..7cad653
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/LearningSourceType.cs
@@ -0,0 +1,19 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// Identifies the origin of a learning entry. Used by <c>DriftEscalationBridge</c>
+/// to filter drift-originated learnings and by audit queries for provenance reporting.
+/// </summary>
+public enum LearningSourceType
+{
+    /// <summary>A human user explicitly corrected agent output.</summary>
+    HumanCorrection,
+    /// <summary>Drift detection identified a quality regression and generated a corrective learning.</summary>
+    DriftDetection,
+    /// <summary>An escalation was resolved with corrections that became a learning.</summary>
+    EscalationResolution,
+    /// <summary>The agent identified its own mistake and self-corrected.</summary>
+    AgentSelfImprovement,
+    /// <summary>A learning was manually entered by an operator or admin.</summary>
+    ManualEntry
+}
diff --git a/src/Content/Domain/Domain.AI/Learnings/WeightedLearning.cs b/src/Content/Domain/Domain.AI/Learnings/WeightedLearning.cs
new file mode 100644
index 0000000..5d3d5c8
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Learnings/WeightedLearning.cs
@@ -0,0 +1,30 @@
+namespace Domain.AI.Learnings;
+
+/// <summary>
+/// A learning entry enriched with computed relevance, feedback, and freshness scores.
+/// Returned by <c>RecallQueryHandler</c> after the full scoring pipeline:
+/// <c>FinalScore = (1 - alpha) * RelevanceScore + alpha * min(FeedbackScore * FreshnessScore, ceiling)</c>.
+/// </summary>
+/// <remarks>
+/// All score fields are pre-computed by the handler. The record is a pure data carrier --
+/// it does not calculate <see cref="FinalScore"/> from the component scores.
+/// </remarks>
+public sealed record WeightedLearning
+{
+    /// <summary>The underlying learning entry.</summary>
+    public required LearningEntry Learning { get; init; }
+
+    /// <summary>Semantic similarity between the recall query and the learning content (0.0-1.0).</summary>
+    public required double RelevanceScore { get; init; }
+
+    /// <summary>The learning's EMA-weighted feedback score (from <see cref="LearningEntry.FeedbackWeight"/>).</summary>
+    public required double FeedbackScore { get; init; }
+
+    /// <summary>Temporal freshness based on decay class and age (0.0-1.0).</summary>
+    public required double FreshnessScore { get; init; }
+
+    /// <summary>
+    /// The blended final score used for ranking. Pre-computed by the recall handler.
+    /// </summary>
+    public required double FinalScore { get; init; }
+}
diff --git a/src/Content/Tests/Domain.AI.Tests/Learnings/LearningsDomainModelTests.cs b/src/Content/Tests/Domain.AI.Tests/Learnings/LearningsDomainModelTests.cs
new file mode 100644
index 0000000..167efd5
--- /dev/null
+++ b/src/Content/Tests/Domain.AI.Tests/Learnings/LearningsDomainModelTests.cs
@@ -0,0 +1,260 @@
+using Domain.AI.Learnings;
+using Xunit;
+
+namespace Domain.AI.Tests.Learnings;
+
+/// <summary>
+/// Tests for learnings domain records and enums.
+/// </summary>
+public sealed class LearningsDomainModelTests
+{
+    // --- LearningCategory enum ---
+
+    [Theory]
+    [InlineData(nameof(LearningCategory.FactualCorrection))]
+    [InlineData(nameof(LearningCategory.StylePreference))]
+    [InlineData(nameof(LearningCategory.ToolUsagePattern))]
+    [InlineData(nameof(LearningCategory.DomainKnowledge))]
+    [InlineData(nameof(LearningCategory.InstructionUpdate))]
+    public void LearningCategory_ContainsExpectedMember(string memberName)
+    {
+        Assert.True(Enum.TryParse<LearningCategory>(memberName, out _));
+    }
+
+    [Fact]
+    public void LearningCategory_HasExactlyFiveMembers()
+    {
+        Assert.Equal(5, Enum.GetValues<LearningCategory>().Length);
+    }
+
+    // --- DecayClass enum ---
+
+    [Fact]
+    public void DecayClass_IntegerValues_MatchExpectedOrdering()
+    {
+        Assert.Equal(0, (int)DecayClass.Volatile);
+        Assert.Equal(1, (int)DecayClass.Stable);
+        Assert.Equal(2, (int)DecayClass.Permanent);
+    }
+
+    // --- LearningSourceType enum ---
+
+    [Theory]
+    [InlineData(nameof(LearningSourceType.HumanCorrection))]
+    [InlineData(nameof(LearningSourceType.DriftDetection))]
+    [InlineData(nameof(LearningSourceType.EscalationResolution))]
+    [InlineData(nameof(LearningSourceType.AgentSelfImprovement))]
+    [InlineData(nameof(LearningSourceType.ManualEntry))]
+    public void LearningSourceType_ContainsExpectedMember(string memberName)
+    {
+        Assert.True(Enum.TryParse<LearningSourceType>(memberName, out _));
+    }
+
+    // --- LearningScope ---
+
+    [Fact]
+    public void LearningScope_WithOnlyAgentId_HasAgentIdSet()
+    {
+        var scope = new LearningScope { AgentId = "agent-1" };
+
+        Assert.Equal("agent-1", scope.AgentId);
+        Assert.Null(scope.TeamId);
+        Assert.False(scope.IsGlobal);
+    }
+
+    [Fact]
+    public void LearningScope_WithOnlyTeamId_HasTeamIdSet()
+    {
+        var scope = new LearningScope { TeamId = "team-1" };
+
+        Assert.Null(scope.AgentId);
+        Assert.Equal("team-1", scope.TeamId);
+        Assert.False(scope.IsGlobal);
+    }
+
+    [Fact]
+    public void LearningScope_WithIsGlobalTrue_AndNoAgentOrTeam()
+    {
+        var scope = new LearningScope { IsGlobal = true };
+
+        Assert.Null(scope.AgentId);
+        Assert.Null(scope.TeamId);
+        Assert.True(scope.IsGlobal);
+    }
+
+    [Fact]
+    public void LearningScope_WithAllThreeSet_AllAccessible()
+    {
+        var scope = new LearningScope { AgentId = "a", TeamId = "t", IsGlobal = true };
+
+        Assert.Equal("a", scope.AgentId);
+        Assert.Equal("t", scope.TeamId);
+        Assert.True(scope.IsGlobal);
+    }
+
+    // --- LearningEntry ---
+
+    [Fact]
+    public void LearningEntry_WithAllFields_RoundTrips()
+    {
+        var id = Guid.NewGuid();
+        var now = DateTimeOffset.UtcNow;
+
+        var entry = new LearningEntry
+        {
+            LearningId = id,
+            Category = LearningCategory.FactualCorrection,
+            DecayClass = DecayClass.Permanent,
+            Scope = new LearningScope { AgentId = "agent-1" },
+            Content = "The API rate limit is 100 req/min, not 1000",
+            Source = new LearningSource
+            {
+                SourceType = LearningSourceType.HumanCorrection,
+                SourceId = "session-42",
+                SourceDescription = "User corrected rate limit info"
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = "human_review",
+                OriginTask = "correction_capture",
+                OriginTimestamp = now,
+                Confidence = 0.95
+            },
+            FeedbackWeight = 2.5,
+            UpdateCount = 3,
+            CreatedAt = now,
+            LastAccessedAt = now.AddMinutes(10),
+            LastReinforcedAt = now.AddMinutes(5),
+            IsDeleted = false,
+            DeleteReason = null
+        };
+
+        Assert.Equal(id, entry.LearningId);
+        Assert.Equal(LearningCategory.FactualCorrection, entry.Category);
+        Assert.Equal(DecayClass.Permanent, entry.DecayClass);
+        Assert.Equal("agent-1", entry.Scope.AgentId);
+        Assert.Contains("rate limit", entry.Content);
+        Assert.Equal(LearningSourceType.HumanCorrection, entry.Source.SourceType);
+        Assert.Equal(0.95, entry.Provenance.Confidence);
+        Assert.Equal(2.5, entry.FeedbackWeight);
+        Assert.Equal(3, entry.UpdateCount);
+        Assert.NotNull(entry.LastAccessedAt);
+        Assert.NotNull(entry.LastReinforcedAt);
+    }
+
+    [Fact]
+    public void LearningEntry_DefaultFeedbackWeight_IsOne()
+    {
+        var entry = CreateMinimalEntry();
+
+        Assert.Equal(1.0, entry.FeedbackWeight);
+        Assert.Equal(0, entry.UpdateCount);
+    }
+
+    [Fact]
+    public void LearningEntry_DefaultOptionalFields_AreNull()
+    {
+        var entry = CreateMinimalEntry();
+
+        Assert.Null(entry.LastAccessedAt);
+        Assert.Null(entry.LastReinforcedAt);
+        Assert.False(entry.IsDeleted);
+        Assert.Null(entry.DeleteReason);
+    }
+
+    // --- WeightedLearning ---
+
+    [Fact]
+    public void WeightedLearning_FinalScore_StoresPreComputedValue()
+    {
+        var entry = CreateMinimalEntry();
+        var weighted = new WeightedLearning
+        {
+            Learning = entry,
+            RelevanceScore = 0.9,
+            FeedbackScore = 1.5,
+            FreshnessScore = 0.8,
+            FinalScore = 0.87
+        };
+
+        Assert.Equal(0.87, weighted.FinalScore);
+        Assert.Equal(0.9, weighted.RelevanceScore);
+        Assert.Equal(1.5, weighted.FeedbackScore);
+        Assert.Equal(0.8, weighted.FreshnessScore);
+        Assert.Equal(entry, weighted.Learning);
+    }
+
+    // --- LearningSource ---
+
+    [Theory]
+    [InlineData(LearningSourceType.HumanCorrection)]
+    [InlineData(LearningSourceType.DriftDetection)]
+    [InlineData(LearningSourceType.EscalationResolution)]
+    [InlineData(LearningSourceType.AgentSelfImprovement)]
+    [InlineData(LearningSourceType.ManualEntry)]
+    public void LearningSource_ConstructionForEachSourceType(LearningSourceType sourceType)
+    {
+        var source = new LearningSource
+        {
+            SourceType = sourceType,
+            SourceId = $"ref-{sourceType}",
+            SourceDescription = $"Created by {sourceType}"
+        };
+
+        Assert.Equal(sourceType, source.SourceType);
+        Assert.Equal($"ref-{sourceType}", source.SourceId);
+    }
+
+    // --- LearningProvenance ---
+
+    [Fact]
+    public void LearningProvenance_WithConfidence_StoresRawValue()
+    {
+        var prov1 = new LearningProvenance
+        {
+            OriginPipeline = "drift_correction",
+            OriginTask = "auto_correct",
+            OriginTimestamp = DateTimeOffset.UtcNow,
+            Confidence = 0.85
+        };
+
+        var prov2 = new LearningProvenance
+        {
+            OriginPipeline = "test",
+            OriginTask = "test",
+            OriginTimestamp = DateTimeOffset.UtcNow,
+            Confidence = 0.0
+        };
+
+        Assert.Equal(0.85, prov1.Confidence);
+        Assert.Equal(0.0, prov2.Confidence);
+    }
+
+    // --- Helpers ---
+
+    private static LearningEntry CreateMinimalEntry()
+    {
+        return new LearningEntry
+        {
+            LearningId = Guid.NewGuid(),
+            Category = LearningCategory.DomainKnowledge,
+            DecayClass = DecayClass.Stable,
+            Scope = new LearningScope { AgentId = "test-agent" },
+            Content = "Test learning content",
+            Source = new LearningSource
+            {
+                SourceType = LearningSourceType.ManualEntry,
+                SourceId = "test",
+                SourceDescription = "Test source"
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = "test",
+                OriginTask = "test",
+                OriginTimestamp = DateTimeOffset.UtcNow,
+                Confidence = 1.0
+            },
+            CreatedAt = DateTimeOffset.UtcNow
+        };
+    }
+}

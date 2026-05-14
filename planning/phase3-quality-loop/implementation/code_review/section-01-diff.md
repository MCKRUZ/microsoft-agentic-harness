diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftAuditRecord.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftAuditRecord.cs
new file mode 100644
index 0000000..c6b7fc5
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftAuditRecord.cs
@@ -0,0 +1,44 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// Discriminator for <see cref="DriftAuditRecord"/> entries.
+/// Determines how the <see cref="DriftAuditRecord.Data"/> field should be interpreted.
+/// </summary>
+public enum DriftAuditRecordType
+{
+    /// <summary>A drift event was detected.</summary>
+    Detected,
+    /// <summary>A drift event was resolved.</summary>
+    Resolved,
+    /// <summary>A baseline was updated (recalculated or adjusted).</summary>
+    BaselineUpdated,
+    /// <summary>An escalation was triggered from a drift event.</summary>
+    EscalationTriggered
+}
+
+/// <summary>
+/// A single audit log entry for a drift detection lifecycle event.
+/// Used by <c>IDriftAuditStore</c> for append-only JSONL persistence.
+/// The <see cref="Data"/> field contains the serialized event data,
+/// discriminated by <see cref="RecordType"/>.
+/// </summary>
+public sealed record DriftAuditRecord
+{
+    /// <summary>Unique identifier for this audit record.</summary>
+    public required Guid RecordId { get; init; }
+
+    /// <summary>Correlates to the originating drift event.</summary>
+    public required Guid EventId { get; init; }
+
+    /// <summary>Discriminator for deserialization of <see cref="Data"/>.</summary>
+    public required DriftAuditRecordType RecordType { get; init; }
+
+    /// <summary>
+    /// Serialized JSON payload containing event-specific data.
+    /// Deserialization target depends on <see cref="RecordType"/>.
+    /// </summary>
+    public required string Data { get; init; }
+
+    /// <summary>When this audit record was created.</summary>
+    public required DateTimeOffset RecordedAt { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftBaseline.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftBaseline.cs
new file mode 100644
index 0000000..e9e7af4
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftBaseline.cs
@@ -0,0 +1,44 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// A "known good" quality snapshot for a scope. Drift scores are compared against
+/// the baseline's per-dimension means and standard deviations to determine whether
+/// quality has degraded.
+/// </summary>
+/// <remarks>
+/// Baselines are stored as knowledge graph nodes with deterministic IDs
+/// (<c>"driftbaseline:{scope}:{identifier}"</c>) for O(1) lookup.
+/// A new baseline overwrites the previous one; history is tracked via
+/// <see cref="DriftAuditRecord"/> entries with <see cref="DriftAuditRecordType.BaselineUpdated"/>.
+/// </remarks>
+public sealed record DriftBaseline
+{
+    /// <summary>Unique identifier for this baseline snapshot.</summary>
+    public required Guid BaselineId { get; init; }
+
+    /// <summary>The hierarchy level of this baseline.</summary>
+    public required DriftScope Scope { get; init; }
+
+    /// <summary>
+    /// Identifies the entity within the scope (agent ID, skill name, or task type name).
+    /// </summary>
+    public required string ScopeIdentifier { get; init; }
+
+    /// <summary>Per-dimension mean scores from the baseline window.</summary>
+    public required IReadOnlyDictionary<DriftDimension, double> Dimensions { get; init; }
+
+    /// <summary>Per-dimension standard deviations from the baseline window.</summary>
+    public required IReadOnlyDictionary<DriftDimension, double> DimensionSigmas { get; init; }
+
+    /// <summary>Number of evaluations used to compute this baseline.</summary>
+    public required int SampleCount { get; init; }
+
+    /// <summary>Start of the rolling window used for this baseline.</summary>
+    public required DateTimeOffset WindowStart { get; init; }
+
+    /// <summary>End of the rolling window used for this baseline.</summary>
+    public required DateTimeOffset WindowEnd { get; init; }
+
+    /// <summary>When this baseline was created.</summary>
+    public required DateTimeOffset CreatedAt { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftDimension.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftDimension.cs
new file mode 100644
index 0000000..b33b16b
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftDimension.cs
@@ -0,0 +1,22 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// Quality scoring dimensions tracked by drift detection.
+/// Each dimension represents an independent axis of agent output quality
+/// that can be measured and compared against a baseline.
+/// </summary>
+public enum DriftDimension
+{
+    /// <summary>Whether agent output is factually consistent with source material.</summary>
+    Faithfulness,
+    /// <summary>Whether agent output addresses the user's actual question/intent.</summary>
+    Relevance,
+    /// <summary>Whether output follows expected structural patterns (formatting, schema).</summary>
+    StructuralConformance,
+    /// <summary>Whether tools are invoked correctly with valid arguments.</summary>
+    ToolUsageAccuracy,
+    /// <summary>Logical consistency and flow within the output.</summary>
+    Coherence,
+    /// <summary>Whether the agent follows system prompt and skill instructions.</summary>
+    InstructionFollowing
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftDimensionScore.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftDimensionScore.cs
new file mode 100644
index 0000000..f8d8bd7
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftDimensionScore.cs
@@ -0,0 +1,23 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// Holds the current vs baseline comparison for a single <see cref="DriftDimension"/>.
+/// Produced by <c>IDriftScorer</c> during drift evaluation.
+/// </summary>
+public sealed record DriftDimensionScore
+{
+    /// <summary>The raw score value from the current evaluation.</summary>
+    public required double CurrentValue { get; init; }
+
+    /// <summary>The baseline mean for this dimension.</summary>
+    public required double BaselineValue { get; init; }
+
+    /// <summary>The EWMA-smoothed value after incorporating this evaluation.</summary>
+    public required double EwmaValue { get; init; }
+
+    /// <summary>
+    /// Deviation from baseline in sigma units. Drives severity classification.
+    /// Computed as <c>abs(ewma - baselineMean) / sigma</c>.
+    /// </summary>
+    public required double Deviation { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftEvent.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftEvent.cs
new file mode 100644
index 0000000..5d7002e
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftEvent.cs
@@ -0,0 +1,23 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// A detected drift occurrence, persisted as a knowledge graph node.
+/// Links to the <see cref="DriftScore"/> that triggered it and optionally
+/// to a <see cref="DriftResolution"/> when the drift is addressed.
+/// </summary>
+public sealed record DriftEvent
+{
+    /// <summary>Unique identifier for this event.</summary>
+    public required Guid EventId { get; init; }
+
+    /// <summary>The drift score that triggered this event.</summary>
+    public required DriftScore DriftScore { get; init; }
+
+    /// <summary>
+    /// How this drift was resolved. Null while the drift is still outstanding.
+    /// </summary>
+    public DriftResolution? Resolution { get; init; }
+
+    /// <summary>When the drift was first detected.</summary>
+    public required DateTimeOffset DetectedAt { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftResolution.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftResolution.cs
new file mode 100644
index 0000000..8f43b54
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftResolution.cs
@@ -0,0 +1,33 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// How a detected drift was ultimately resolved.
+/// </summary>
+public enum DriftResolutionType
+{
+    /// <summary>A learning entry was applied that corrected the drift cause.</summary>
+    LearningApplied,
+    /// <summary>The baseline was adjusted to reflect intentional quality changes.</summary>
+    BaselineAdjusted,
+    /// <summary>An operator manually dismissed the drift as a false positive.</summary>
+    ManualDismissal,
+    /// <summary>A Phase 2 escalation resolved the underlying issue.</summary>
+    EscalationResolved
+}
+
+/// <summary>
+/// Records how and when a <see cref="DriftEvent"/> was resolved.
+/// </summary>
+public sealed record DriftResolution
+{
+    /// <summary>The mechanism by which this drift was resolved.</summary>
+    public required DriftResolutionType ResolvedBy { get; init; }
+
+    /// <summary>
+    /// Identifier linking to the resolving entity (learning ID, escalation ID, etc.).
+    /// </summary>
+    public required string ResolutionId { get; init; }
+
+    /// <summary>When the drift was resolved.</summary>
+    public required DateTimeOffset ResolvedAt { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftScope.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftScope.cs
new file mode 100644
index 0000000..df9b134
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftScope.cs
@@ -0,0 +1,15 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// Hierarchy level at which a drift baseline is defined.
+/// Baselines cascade: TaskType -> Skill -> Agent (most specific wins).
+/// </summary>
+public enum DriftScope
+{
+    /// <summary>Agent-wide baseline (broadest scope).</summary>
+    Agent,
+    /// <summary>Skill-specific baseline.</summary>
+    Skill,
+    /// <summary>Task-type-specific baseline (most granular).</summary>
+    TaskType
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftScore.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftScore.cs
new file mode 100644
index 0000000..63985c3
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftScore.cs
@@ -0,0 +1,35 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// The drift measurement result for a single evaluation, comparing per-dimension
+/// scores against a <see cref="DriftBaseline"/>. Produced by <c>IDriftDetectionService</c>.
+/// </summary>
+public sealed record DriftScore
+{
+    /// <summary>Unique identifier for this score.</summary>
+    public required Guid ScoreId { get; init; }
+
+    /// <summary>The baseline this score was compared against.</summary>
+    public required Guid BaselineId { get; init; }
+
+    /// <summary>The scope of the evaluation.</summary>
+    public required DriftScope Scope { get; init; }
+
+    /// <summary>Identifies the entity within the scope.</summary>
+    public required string ScopeIdentifier { get; init; }
+
+    /// <summary>Per-dimension comparison results.</summary>
+    public required IReadOnlyDictionary<DriftDimension, DriftDimensionScore> Dimensions { get; init; }
+
+    /// <summary>
+    /// Maximum deviation across all dimensions (in sigma units).
+    /// This single value drives the severity classification.
+    /// </summary>
+    public required double OverallDrift { get; init; }
+
+    /// <summary>Classified severity based on threshold configuration.</summary>
+    public required DriftSeverity Severity { get; init; }
+
+    /// <summary>When this score was computed.</summary>
+    public required DateTimeOffset ScoredAt { get; init; }
+}
diff --git a/src/Content/Domain/Domain.AI/DriftDetection/DriftSeverity.cs b/src/Content/Domain/Domain.AI/DriftDetection/DriftSeverity.cs
new file mode 100644
index 0000000..a6c5b8b
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/DriftDetection/DriftSeverity.cs
@@ -0,0 +1,17 @@
+namespace Domain.AI.DriftDetection;
+
+/// <summary>
+/// Tiered severity levels for detected drift, driving escalation behavior.
+/// Integer values encode ordering so that <c>DriftSeverity.Warn &lt; DriftSeverity.Alert</c> is valid.
+/// </summary>
+public enum DriftSeverity
+{
+    /// <summary>No significant drift detected.</summary>
+    None = 0,
+    /// <summary>Drift exceeds warning threshold. Logged and notified.</summary>
+    Warn = 1,
+    /// <summary>Drift exceeds alert threshold. Requires attention.</summary>
+    Alert = 2,
+    /// <summary>Drift exceeds escalation threshold. Triggers Phase 2 escalation if enabled.</summary>
+    Escalate = 3
+}
diff --git a/src/Content/Tests/Domain.AI.Tests/DriftDetection/DriftDetectionDomainModelTests.cs b/src/Content/Tests/Domain.AI.Tests/DriftDetection/DriftDetectionDomainModelTests.cs
new file mode 100644
index 0000000..f8ecdff
--- /dev/null
+++ b/src/Content/Tests/Domain.AI.Tests/DriftDetection/DriftDetectionDomainModelTests.cs
@@ -0,0 +1,318 @@
+using System.Text.Json;
+using Domain.AI.DriftDetection;
+using Xunit;
+
+namespace Domain.AI.Tests.DriftDetection;
+
+/// <summary>
+/// Tests for drift detection domain records, enums, and their properties.
+/// </summary>
+public sealed class DriftDetectionDomainModelTests
+{
+    // --- DriftDimension enum ---
+
+    [Fact]
+    public void DriftDimension_HasExactlySixMembers()
+    {
+        var values = Enum.GetValues<DriftDimension>();
+        Assert.Equal(6, values.Length);
+    }
+
+    [Theory]
+    [InlineData(nameof(DriftDimension.Faithfulness))]
+    [InlineData(nameof(DriftDimension.Relevance))]
+    [InlineData(nameof(DriftDimension.StructuralConformance))]
+    [InlineData(nameof(DriftDimension.ToolUsageAccuracy))]
+    [InlineData(nameof(DriftDimension.Coherence))]
+    [InlineData(nameof(DriftDimension.InstructionFollowing))]
+    public void DriftDimension_ContainsExpectedMember(string memberName)
+    {
+        Assert.True(Enum.TryParse<DriftDimension>(memberName, out _));
+    }
+
+    // --- DriftSeverity enum ---
+
+    [Fact]
+    public void DriftSeverity_IntegerCasting_MaintainsOrdering()
+    {
+        Assert.Equal(0, (int)DriftSeverity.None);
+        Assert.Equal(1, (int)DriftSeverity.Warn);
+        Assert.Equal(2, (int)DriftSeverity.Alert);
+        Assert.Equal(3, (int)DriftSeverity.Escalate);
+        Assert.True(DriftSeverity.None < DriftSeverity.Warn);
+        Assert.True(DriftSeverity.Warn < DriftSeverity.Alert);
+        Assert.True(DriftSeverity.Alert < DriftSeverity.Escalate);
+    }
+
+    // --- DriftScope enum ---
+
+    [Theory]
+    [InlineData(nameof(DriftScope.Agent))]
+    [InlineData(nameof(DriftScope.Skill))]
+    [InlineData(nameof(DriftScope.TaskType))]
+    public void DriftScope_ContainsExpectedMember(string memberName)
+    {
+        Assert.True(Enum.TryParse<DriftScope>(memberName, out _));
+    }
+
+    // --- DriftDimensionScore ---
+
+    [Fact]
+    public void DriftDimensionScore_Construction_RoundTripsAllProperties()
+    {
+        var score = new DriftDimensionScore
+        {
+            CurrentValue = 0.7,
+            BaselineValue = 0.85,
+            EwmaValue = 0.78,
+            Deviation = 1.5
+        };
+
+        Assert.Equal(0.7, score.CurrentValue);
+        Assert.Equal(0.85, score.BaselineValue);
+        Assert.Equal(0.78, score.EwmaValue);
+        Assert.Equal(1.5, score.Deviation);
+    }
+
+    [Fact]
+    public void DriftDimensionScore_Deviation_StoresValueFromScorer()
+    {
+        var score = new DriftDimensionScore
+        {
+            CurrentValue = 0.5,
+            BaselineValue = 0.9,
+            EwmaValue = 0.6,
+            Deviation = 3.2
+        };
+
+        Assert.Equal(3.2, score.Deviation);
+    }
+
+    // --- DriftBaseline ---
+
+    [Fact]
+    public void DriftBaseline_Construction_SetsAllProperties()
+    {
+        var id = Guid.NewGuid();
+        var now = DateTimeOffset.UtcNow;
+        var dimensions = new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.9,
+            [DriftDimension.Relevance] = 0.85
+        };
+        var sigmas = new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.05,
+            [DriftDimension.Relevance] = 0.07
+        };
+
+        var baseline = new DriftBaseline
+        {
+            BaselineId = id,
+            Scope = DriftScope.Agent,
+            ScopeIdentifier = "agent-1",
+            Dimensions = dimensions.AsReadOnly(),
+            DimensionSigmas = sigmas.AsReadOnly(),
+            SampleCount = 50,
+            WindowStart = now.AddDays(-7),
+            WindowEnd = now,
+            CreatedAt = now
+        };
+
+        Assert.Equal(id, baseline.BaselineId);
+        Assert.Equal(DriftScope.Agent, baseline.Scope);
+        Assert.Equal("agent-1", baseline.ScopeIdentifier);
+        Assert.Equal(2, baseline.Dimensions.Count);
+        Assert.Equal(0.9, baseline.Dimensions[DriftDimension.Faithfulness]);
+        Assert.Equal(50, baseline.SampleCount);
+    }
+
+    [Fact]
+    public void DriftBaseline_Dimensions_AreIReadOnlyDictionary()
+    {
+        var baseline = new DriftBaseline
+        {
+            BaselineId = Guid.NewGuid(),
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "summarize",
+            Dimensions = new Dictionary<DriftDimension, double>().AsReadOnly(),
+            DimensionSigmas = new Dictionary<DriftDimension, double>().AsReadOnly(),
+            SampleCount = 10,
+            WindowStart = DateTimeOffset.UtcNow.AddDays(-1),
+            WindowEnd = DateTimeOffset.UtcNow,
+            CreatedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.IsAssignableFrom<IReadOnlyDictionary<DriftDimension, double>>(baseline.Dimensions);
+        Assert.IsAssignableFrom<IReadOnlyDictionary<DriftDimension, double>>(baseline.DimensionSigmas);
+    }
+
+    // --- DriftScore ---
+
+    [Fact]
+    public void DriftScore_Severity_RoundTripsExpectedValue()
+    {
+        var score = CreateDriftScore(DriftSeverity.Alert, 2.5);
+        Assert.Equal(DriftSeverity.Alert, score.Severity);
+    }
+
+    [Fact]
+    public void DriftScore_OverallDrift_StoresMaxDeviation()
+    {
+        var score = CreateDriftScore(DriftSeverity.Warn, 1.8);
+        Assert.Equal(1.8, score.OverallDrift);
+    }
+
+    // --- DriftEvent ---
+
+    [Fact]
+    public void DriftEvent_NullResolution_RepresentsUnresolvedDrift()
+    {
+        var driftScore = CreateDriftScore(DriftSeverity.Alert, 2.0);
+        var evt = new DriftEvent
+        {
+            EventId = Guid.NewGuid(),
+            DriftScore = driftScore,
+            Resolution = null,
+            DetectedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.Null(evt.Resolution);
+        Assert.NotEqual(Guid.Empty, evt.EventId);
+        Assert.Equal(driftScore, evt.DriftScore);
+    }
+
+    [Fact]
+    public void DriftEvent_WithResolution_RepresentsResolvedDrift()
+    {
+        var driftScore = CreateDriftScore(DriftSeverity.Warn, 1.5);
+        var resolution = new DriftResolution
+        {
+            ResolvedBy = DriftResolutionType.LearningApplied,
+            ResolutionId = "learning-42",
+            ResolvedAt = DateTimeOffset.UtcNow
+        };
+
+        var evt = new DriftEvent
+        {
+            EventId = Guid.NewGuid(),
+            DriftScore = driftScore,
+            Resolution = resolution,
+            DetectedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
+        };
+
+        Assert.NotNull(evt.Resolution);
+        Assert.Equal(DriftResolutionType.LearningApplied, evt.Resolution.ResolvedBy);
+    }
+
+    // --- DriftResolution ---
+
+    [Theory]
+    [InlineData(nameof(DriftResolutionType.LearningApplied))]
+    [InlineData(nameof(DriftResolutionType.BaselineAdjusted))]
+    [InlineData(nameof(DriftResolutionType.ManualDismissal))]
+    [InlineData(nameof(DriftResolutionType.EscalationResolved))]
+    public void DriftResolutionType_ContainsExpectedMember(string memberName)
+    {
+        Assert.True(Enum.TryParse<DriftResolutionType>(memberName, out _));
+    }
+
+    [Theory]
+    [InlineData(DriftResolutionType.LearningApplied)]
+    [InlineData(DriftResolutionType.BaselineAdjusted)]
+    [InlineData(DriftResolutionType.ManualDismissal)]
+    [InlineData(DriftResolutionType.EscalationResolved)]
+    public void DriftResolution_Construction_WithEachType(DriftResolutionType type)
+    {
+        var resolution = new DriftResolution
+        {
+            ResolvedBy = type,
+            ResolutionId = $"ref-{type}",
+            ResolvedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.Equal(type, resolution.ResolvedBy);
+        Assert.Equal($"ref-{type}", resolution.ResolutionId);
+    }
+
+    // --- DriftAuditRecord ---
+
+    [Theory]
+    [InlineData(nameof(DriftAuditRecordType.Detected))]
+    [InlineData(nameof(DriftAuditRecordType.Resolved))]
+    [InlineData(nameof(DriftAuditRecordType.BaselineUpdated))]
+    [InlineData(nameof(DriftAuditRecordType.EscalationTriggered))]
+    public void DriftAuditRecordType_ContainsExpectedMember(string memberName)
+    {
+        Assert.True(Enum.TryParse<DriftAuditRecordType>(memberName, out _));
+    }
+
+    [Fact]
+    public void DriftAuditRecord_Construction_WithJsonPayload()
+    {
+        var eventId = Guid.NewGuid();
+        var data = JsonSerializer.Serialize(new { dimension = "Faithfulness", deviation = 2.1 });
+
+        var record = new DriftAuditRecord
+        {
+            RecordId = Guid.NewGuid(),
+            EventId = eventId,
+            RecordType = DriftAuditRecordType.Detected,
+            Data = data,
+            RecordedAt = DateTimeOffset.UtcNow
+        };
+
+        Assert.Equal(eventId, record.EventId);
+        Assert.Equal(DriftAuditRecordType.Detected, record.RecordType);
+        Assert.Contains("Faithfulness", record.Data);
+    }
+
+    [Fact]
+    public void DriftAuditRecord_JsonRoundTrip_PreservesEquality()
+    {
+        var record = new DriftAuditRecord
+        {
+            RecordId = Guid.NewGuid(),
+            EventId = Guid.NewGuid(),
+            RecordType = DriftAuditRecordType.Resolved,
+            Data = """{"resolution":"baseline_adjusted"}""",
+            RecordedAt = DateTimeOffset.UtcNow
+        };
+
+        var json = JsonSerializer.Serialize(record);
+        var deserialized = JsonSerializer.Deserialize<DriftAuditRecord>(json);
+
+        Assert.NotNull(deserialized);
+        Assert.Equal(record.RecordId, deserialized.RecordId);
+        Assert.Equal(record.EventId, deserialized.EventId);
+        Assert.Equal(record.RecordType, deserialized.RecordType);
+        Assert.Equal(record.Data, deserialized.Data);
+        Assert.Equal(record.RecordedAt, deserialized.RecordedAt);
+    }
+
+    // --- Helpers ---
+
+    private static DriftScore CreateDriftScore(DriftSeverity severity, double overallDrift)
+    {
+        return new DriftScore
+        {
+            ScoreId = Guid.NewGuid(),
+            BaselineId = Guid.NewGuid(),
+            Scope = DriftScope.Agent,
+            ScopeIdentifier = "test-agent",
+            Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>
+            {
+                [DriftDimension.Faithfulness] = new DriftDimensionScore
+                {
+                    CurrentValue = 0.7,
+                    BaselineValue = 0.9,
+                    EwmaValue = 0.75,
+                    Deviation = overallDrift
+                }
+            }.AsReadOnly(),
+            OverallDrift = overallDrift,
+            Severity = severity,
+            ScoredAt = DateTimeOffset.UtcNow
+        };
+    }
+}

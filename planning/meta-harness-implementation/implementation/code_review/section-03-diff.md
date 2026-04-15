diff --git a/src/Content/Domain/Domain.Common/MetaHarness/ExecutionTraceRecord.cs b/src/Content/Domain/Domain.Common/MetaHarness/ExecutionTraceRecord.cs
new file mode 100644
index 0000000..b12f17f
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/ExecutionTraceRecord.cs
@@ -0,0 +1,68 @@
+using System.Text.Json.Serialization;
+
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Represents one JSONL line in <c>traces.jsonl</c>.
+/// Uses <see cref="JsonPropertyNameAttribute"/> attributes for snake_case serialization.
+/// </summary>
+public sealed record ExecutionTraceRecord
+{
+    [JsonPropertyName("seq")]
+    public long Seq { get; init; }
+
+    [JsonPropertyName("ts")]
+    public DateTimeOffset Ts { get; init; }
+
+    [JsonPropertyName("type")]
+    public string Type { get; init; } = string.Empty;
+
+    [JsonPropertyName("execution_run_id")]
+    public string ExecutionRunId { get; init; } = string.Empty;
+
+    [JsonPropertyName("candidate_id")]
+    public string? CandidateId { get; init; }
+
+    [JsonPropertyName("iteration")]
+    public int? Iteration { get; init; }
+
+    [JsonPropertyName("task_id")]
+    public string? TaskId { get; init; }
+
+    [JsonPropertyName("turn_id")]
+    public string TurnId { get; init; } = string.Empty;
+
+    [JsonPropertyName("tool_name")]
+    public string? ToolName { get; init; }
+
+    [JsonPropertyName("result_category")]
+    public string? ResultCategory { get; init; }
+
+    [JsonPropertyName("payload_summary")]
+    public string? PayloadSummary { get; init; }
+
+    [JsonPropertyName("payload_full_path")]
+    public string? PayloadFullPath { get; init; }
+
+    [JsonPropertyName("redacted")]
+    public bool? Redacted { get; init; }
+}
+
+/// <summary>Valid values for <see cref="ExecutionTraceRecord.Type"/>.</summary>
+public static class TraceRecordTypes
+{
+    public const string ToolCall = "tool_call";
+    public const string ToolResult = "tool_result";
+    public const string Decision = "decision";
+    public const string Observation = "observation";
+}
+
+/// <summary>Valid values for <see cref="ExecutionTraceRecord.ResultCategory"/>.</summary>
+public static class TraceResultCategories
+{
+    public const string Success = "success";
+    public const string Partial = "partial";
+    public const string Error = "error";
+    public const string Timeout = "timeout";
+    public const string Blocked = "blocked";
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/HarnessScores.cs b/src/Content/Domain/Domain.Common/MetaHarness/HarnessScores.cs
new file mode 100644
index 0000000..2dc7f22
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/HarnessScores.cs
@@ -0,0 +1,29 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Represents the contents of <c>scores.json</c>.
+/// Immutable record; no behavior beyond what <c>record</c> provides.
+/// </summary>
+public sealed record HarnessScores
+{
+    /// <summary>Pass rate in the range 0.0–1.0.</summary>
+    public double PassRate { get; init; }
+
+    /// <summary>Cumulative token cost for this run.</summary>
+    public long TotalTokenCost { get; init; }
+
+    /// <summary>Per-task pass/fail breakdown.</summary>
+    public IReadOnlyList<ExampleResult> PerExampleResults { get; init; } =
+        Array.Empty<ExampleResult>();
+
+    /// <summary>When scoring was completed.</summary>
+    public DateTimeOffset ScoredAt { get; init; }
+}
+
+/// <summary>Per-task pass/fail result within <see cref="HarnessScores"/>.</summary>
+public sealed record ExampleResult
+{
+    public string TaskId { get; init; } = string.Empty;
+    public bool Passed { get; init; }
+    public long TokenCost { get; init; }
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/RunMetadata.cs b/src/Content/Domain/Domain.Common/MetaHarness/RunMetadata.cs
new file mode 100644
index 0000000..46a9877
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/RunMetadata.cs
@@ -0,0 +1,29 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Metadata written to <c>manifest.json</c> when a run is started.
+/// Immutable record; no behavior beyond what <c>record</c> provides.
+/// </summary>
+public sealed record RunMetadata
+{
+    /// <summary>When the run started.</summary>
+    public DateTimeOffset StartedAt { get; init; }
+
+    /// <summary>Name of the agent being traced.</summary>
+    public string AgentName { get; init; } = string.Empty;
+
+    /// <summary>Optional human-readable description of the task.</summary>
+    public string? TaskDescription { get; init; }
+
+    /// <summary>Set for optimization eval runs.</summary>
+    public Guid? CandidateId { get; init; }
+
+    /// <summary>Set for optimization eval runs.</summary>
+    public Guid? OptimizationRunId { get; init; }
+
+    /// <summary>Set for optimization eval runs.</summary>
+    public int? Iteration { get; init; }
+
+    /// <summary>Set for eval task runs.</summary>
+    public string? TaskId { get; init; }
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/TraceScope.cs b/src/Content/Domain/Domain.Common/MetaHarness/TraceScope.cs
new file mode 100644
index 0000000..79d43ac
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/TraceScope.cs
@@ -0,0 +1,45 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Encodes the three-tier identity (OptimizationRun → Candidate → Execution) and
+/// resolves filesystem paths for trace output. Immutable record; no I/O.
+/// </summary>
+public sealed record TraceScope
+{
+    /// <summary>Always required. Identifies a single agent execution.</summary>
+    public Guid ExecutionRunId { get; init; }
+
+    /// <summary>The outer loop run. Null for non-optimization agent runs.</summary>
+    public Guid? OptimizationRunId { get; init; }
+
+    /// <summary>One proposed harness configuration. Null for non-optimization runs.</summary>
+    public Guid? CandidateId { get; init; }
+
+    /// <summary>Which eval task this execution belongs to. Null for non-eval runs.</summary>
+    public string? TaskId { get; init; }
+
+    /// <summary>Creates a standalone execution scope (non-optimization agent run).</summary>
+    public static TraceScope ForExecution(Guid executionRunId) => new() { ExecutionRunId = executionRunId };
+
+    /// <summary>
+    /// Returns the absolute directory path for this scope under <paramref name="traceRoot"/>.
+    /// Pure string operation — no I/O.
+    /// </summary>
+    public string ResolveDirectory(string traceRoot)
+    {
+        if (!OptimizationRunId.HasValue)
+            return Path.Combine(traceRoot, "executions", ExecutionRunId.ToString("D").ToLowerInvariant());
+
+        var optPath = Path.Combine(traceRoot, "optimizations", OptimizationRunId.Value.ToString("D").ToLowerInvariant());
+
+        if (!CandidateId.HasValue)
+            return optPath;
+
+        var candidatePath = Path.Combine(optPath, "candidates", CandidateId.Value.ToString("D").ToLowerInvariant());
+
+        if (TaskId is null)
+            return candidatePath;
+
+        return Path.Combine(candidatePath, "eval", TaskId, ExecutionRunId.ToString("D").ToLowerInvariant());
+    }
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/TurnArtifacts.cs b/src/Content/Domain/Domain.Common/MetaHarness/TurnArtifacts.cs
new file mode 100644
index 0000000..2e253be
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/TurnArtifacts.cs
@@ -0,0 +1,29 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Represents everything written to a <c>turns/{n}/</c> subdirectory for a single agent turn.
+/// All properties are nullable — a turn artifact may contain only a subset of files.
+/// </summary>
+public sealed record TurnArtifacts
+{
+    /// <summary>1-based turn index.</summary>
+    public int TurnNumber { get; init; }
+
+    /// <summary>Contents of <c>system_prompt.md</c>.</summary>
+    public string? SystemPrompt { get; init; }
+
+    /// <summary>Raw JSONL string written to <c>tool_calls.jsonl</c>.</summary>
+    public string? ToolCallsJsonl { get; init; }
+
+    /// <summary>Contents of <c>model_response.md</c>.</summary>
+    public string? ModelResponse { get; init; }
+
+    /// <summary>JSON string written to <c>state_snapshot.json</c>.</summary>
+    public string? StateSnapshot { get; init; }
+
+    /// <summary>
+    /// Map of <c>callId → serialized result</c> written to <c>tool_results/{callId}.json</c>.
+    /// </summary>
+    public IReadOnlyDictionary<string, string> ToolResults { get; init; } =
+        new Dictionary<string, string>();
+}
diff --git a/src/Content/Tests/Domain.Common.Tests/MetaHarness/TraceScopeTests.cs b/src/Content/Tests/Domain.Common.Tests/MetaHarness/TraceScopeTests.cs
new file mode 100644
index 0000000..e921fd7
--- /dev/null
+++ b/src/Content/Tests/Domain.Common.Tests/MetaHarness/TraceScopeTests.cs
@@ -0,0 +1,102 @@
+using Domain.Common.MetaHarness;
+using FluentAssertions;
+using Xunit;
+
+namespace Domain.Common.Tests.MetaHarness;
+
+public class TraceScopeTests
+{
+    [Fact]
+    public void ForExecution_CreatesScope_WithNullOptimizationAndCandidateIds()
+    {
+        var id = Guid.NewGuid();
+
+        var scope = TraceScope.ForExecution(id);
+
+        scope.ExecutionRunId.Should().Be(id);
+        scope.OptimizationRunId.Should().BeNull();
+        scope.CandidateId.Should().BeNull();
+        scope.TaskId.Should().BeNull();
+    }
+
+    [Fact]
+    public void ResolveDirectory_WithExecutionOnlyScope_ResolvesUnderExecutions()
+    {
+        var id = Guid.NewGuid();
+        var scope = TraceScope.ForExecution(id);
+
+        var dir = scope.ResolveDirectory("/traces");
+
+        dir.Should().Be(Path.Combine("/traces", "executions", id.ToString("D").ToLowerInvariant()));
+    }
+
+    [Fact]
+    public void ResolveDirectory_WithAllIds_ResolvesToCorrectDirectoryPath()
+    {
+        var optRunId = Guid.NewGuid();
+        var candidateId = Guid.NewGuid();
+        var execId = Guid.NewGuid();
+        var scope = new TraceScope
+        {
+            ExecutionRunId = execId,
+            OptimizationRunId = optRunId,
+            CandidateId = candidateId,
+            TaskId = "task-01"
+        };
+
+        var dir = scope.ResolveDirectory("/traces");
+
+        var expected = Path.Combine(
+            "/traces", "optimizations", optRunId.ToString("D").ToLowerInvariant(),
+            "candidates", candidateId.ToString("D").ToLowerInvariant(),
+            "eval", "task-01", execId.ToString("D").ToLowerInvariant());
+        dir.Should().Be(expected);
+    }
+
+    [Fact]
+    public void ResolveDirectory_WithOptimizationOnlyScope_ResolvesUnderOptimizations()
+    {
+        var optRunId = Guid.NewGuid();
+        var scope = new TraceScope
+        {
+            ExecutionRunId = Guid.NewGuid(),
+            OptimizationRunId = optRunId
+        };
+
+        var dir = scope.ResolveDirectory("/traces");
+
+        dir.Should().Be(Path.Combine("/traces", "optimizations", optRunId.ToString("D").ToLowerInvariant()));
+    }
+
+    [Fact]
+    public void ResolveDirectory_WithOptimizationAndCandidateButNoTask_ResolvesCorrectly()
+    {
+        var optRunId = Guid.NewGuid();
+        var candidateId = Guid.NewGuid();
+        var scope = new TraceScope
+        {
+            ExecutionRunId = Guid.NewGuid(),
+            OptimizationRunId = optRunId,
+            CandidateId = candidateId
+        };
+
+        var dir = scope.ResolveDirectory("/traces");
+
+        var expected = Path.Combine(
+            "/traces", "optimizations", optRunId.ToString("D").ToLowerInvariant(),
+            "candidates", candidateId.ToString("D").ToLowerInvariant());
+        dir.Should().Be(expected);
+    }
+
+    [Fact]
+    public void TraceScope_WithExpression_DoesNotMutateOriginal()
+    {
+        var original = TraceScope.ForExecution(Guid.NewGuid());
+        var optRunId = Guid.NewGuid();
+
+        var modified = original with { OptimizationRunId = optRunId };
+
+        original.OptimizationRunId.Should().BeNull();
+        modified.OptimizationRunId.Should().Be(optRunId);
+    }
+}

diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftAuditQuery.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftAuditQuery.cs
new file mode 100644
index 0000000..71c93bd
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftAuditQuery.cs
@@ -0,0 +1,21 @@
+using Domain.AI.DriftDetection;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Query DTO for retrieving drift audit records. All fields are optional filters.
+/// </summary>
+public sealed record DriftAuditQuery
+{
+    /// <summary>Start of the query window. When both Start and End are provided, Start must be before End.</summary>
+    public DateTimeOffset? Start { get; init; }
+
+    /// <summary>End of the query window.</summary>
+    public DateTimeOffset? End { get; init; }
+
+    /// <summary>Filter by audit record type.</summary>
+    public DriftAuditRecordType? RecordType { get; init; }
+
+    /// <summary>Filter by originating drift event ID.</summary>
+    public Guid? EventId { get; init; }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftAuditQueryValidator.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftAuditQueryValidator.cs
new file mode 100644
index 0000000..6fe4bd6
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftAuditQueryValidator.cs
@@ -0,0 +1,17 @@
+using FluentValidation;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Validates <see cref="DriftAuditQuery"/> ensuring Start is before End when both are provided.
+/// </summary>
+public sealed class DriftAuditQueryValidator : AbstractValidator<DriftAuditQuery>
+{
+    public DriftAuditQueryValidator()
+    {
+        RuleFor(x => x.Start)
+            .LessThan(x => x.End)
+            .When(x => x.Start.HasValue && x.End.HasValue)
+            .WithMessage("Start must be before End.");
+    }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftBaselineUpdateRequest.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftBaselineUpdateRequest.cs
new file mode 100644
index 0000000..89c94d7
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftBaselineUpdateRequest.cs
@@ -0,0 +1,15 @@
+using Domain.AI.DriftDetection;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Request DTO for recalculating a drift baseline from recent evaluation history.
+/// </summary>
+public sealed record DriftBaselineUpdateRequest
+{
+    /// <summary>The hierarchy level of the baseline to update.</summary>
+    public required DriftScope Scope { get; init; }
+
+    /// <summary>Identifies the entity within the scope (agent ID, skill name, or task type).</summary>
+    public required string ScopeIdentifier { get; init; }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftBaselineUpdateRequestValidator.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftBaselineUpdateRequestValidator.cs
new file mode 100644
index 0000000..03b15ca
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftBaselineUpdateRequestValidator.cs
@@ -0,0 +1,15 @@
+using FluentValidation;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Validates <see cref="DriftBaselineUpdateRequest"/> ensuring scope identifier is provided.
+/// </summary>
+public sealed class DriftBaselineUpdateRequestValidator : AbstractValidator<DriftBaselineUpdateRequest>
+{
+    public DriftBaselineUpdateRequestValidator()
+    {
+        RuleFor(x => x.ScopeIdentifier)
+            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.");
+    }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftEvaluationRequest.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftEvaluationRequest.cs
new file mode 100644
index 0000000..b671deb
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftEvaluationRequest.cs
@@ -0,0 +1,19 @@
+using Domain.AI.DriftDetection;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Request DTO for evaluating drift against a baseline.
+/// Contains the current dimension scores to compare.
+/// </summary>
+public sealed record DriftEvaluationRequest
+{
+    /// <summary>The hierarchy level of the evaluation.</summary>
+    public required DriftScope Scope { get; init; }
+
+    /// <summary>Identifies the entity within the scope (agent ID, skill name, or task type).</summary>
+    public required string ScopeIdentifier { get; init; }
+
+    /// <summary>Current dimension scores to evaluate against the baseline.</summary>
+    public required IReadOnlyDictionary<DriftDimension, double> Dimensions { get; init; }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftEvaluationRequestValidator.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftEvaluationRequestValidator.cs
new file mode 100644
index 0000000..87117d9
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftEvaluationRequestValidator.cs
@@ -0,0 +1,18 @@
+using FluentValidation;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Validates <see cref="DriftEvaluationRequest"/> ensuring scope identifier and dimensions are provided.
+/// </summary>
+public sealed class DriftEvaluationRequestValidator : AbstractValidator<DriftEvaluationRequest>
+{
+    public DriftEvaluationRequestValidator()
+    {
+        RuleFor(x => x.ScopeIdentifier)
+            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.");
+
+        RuleFor(x => x.Dimensions)
+            .NotEmpty().WithMessage("At least one dimension must be provided.");
+    }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftHistoryQuery.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftHistoryQuery.cs
new file mode 100644
index 0000000..b20f083
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftHistoryQuery.cs
@@ -0,0 +1,21 @@
+using Domain.AI.DriftDetection;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Query DTO for retrieving historical drift scores within a time window.
+/// </summary>
+public sealed record DriftHistoryQuery
+{
+    /// <summary>The hierarchy level to query.</summary>
+    public required DriftScope Scope { get; init; }
+
+    /// <summary>Identifies the entity within the scope.</summary>
+    public required string ScopeIdentifier { get; init; }
+
+    /// <summary>Start of the query window (inclusive).</summary>
+    public required DateTimeOffset Start { get; init; }
+
+    /// <summary>End of the query window (inclusive).</summary>
+    public required DateTimeOffset End { get; init; }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftHistoryQueryValidator.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftHistoryQueryValidator.cs
new file mode 100644
index 0000000..f817c2a
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftHistoryQueryValidator.cs
@@ -0,0 +1,19 @@
+using FluentValidation;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Validates <see cref="DriftHistoryQuery"/> ensuring scope identifier is provided
+/// and the time window is ordered correctly.
+/// </summary>
+public sealed class DriftHistoryQueryValidator : AbstractValidator<DriftHistoryQuery>
+{
+    public DriftHistoryQueryValidator()
+    {
+        RuleFor(x => x.ScopeIdentifier)
+            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.");
+
+        RuleFor(x => x.Start)
+            .LessThan(x => x.End).WithMessage("Start must be before End.");
+    }
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/EwmaState.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/EwmaState.cs
new file mode 100644
index 0000000..44a0277
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/EwmaState.cs
@@ -0,0 +1,33 @@
+using Domain.AI.DriftDetection;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Persisted EWMA running state for a single scope+dimension combination.
+/// Stored as a knowledge graph node using <see cref="DeterministicId"/> for O(1) lookup.
+/// </summary>
+public sealed record EwmaState
+{
+    /// <summary>The hierarchy level of this EWMA state.</summary>
+    public required DriftScope Scope { get; init; }
+
+    /// <summary>Identifies the entity within the scope.</summary>
+    public required string ScopeIdentifier { get; init; }
+
+    /// <summary>The quality dimension this state tracks.</summary>
+    public required DriftDimension Dimension { get; init; }
+
+    /// <summary>The current EWMA-smoothed value.</summary>
+    public required double CurrentEwma { get; init; }
+
+    /// <summary>Number of samples incorporated into this EWMA.</summary>
+    public required int SampleCount { get; init; }
+
+    /// <summary>When this state was last updated.</summary>
+    public required DateTimeOffset LastUpdatedAt { get; init; }
+
+    /// <summary>
+    /// Deterministic ID for graph node storage: "ewma:{Scope}:{ScopeIdentifier}:{Dimension}".
+    /// </summary>
+    public string DeterministicId => $"ewma:{Scope}:{ScopeIdentifier}:{Dimension}";
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftAuditStore.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftAuditStore.cs
new file mode 100644
index 0000000..9da5ccf
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftAuditStore.cs
@@ -0,0 +1,17 @@
+using Domain.AI.DriftDetection;
+using Domain.Common;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Append-only persistence for drift audit records.
+/// Supports JSONL-backed storage for compliance and debugging.
+/// </summary>
+public interface IDriftAuditStore
+{
+    /// <summary>Appends an audit record to the store.</summary>
+    Task<Result> RecordAsync(DriftAuditRecord record, CancellationToken ct);
+
+    /// <summary>Queries audit records matching the specified filters.</summary>
+    Task<Result<IReadOnlyList<DriftAuditRecord>>> GetRecordsAsync(DriftAuditQuery query, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftBaselineStore.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftBaselineStore.cs
new file mode 100644
index 0000000..534b2e9
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftBaselineStore.cs
@@ -0,0 +1,20 @@
+using Domain.AI.DriftDetection;
+using Domain.Common;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Persistence contract for drift baselines.
+/// Keyed DI: <c>"graph"</c> (default), <c>"in_memory"</c> (testing).
+/// </summary>
+public interface IDriftBaselineStore
+{
+    /// <summary>Persists a baseline snapshot, overwriting any previous baseline for the same scope+identifier.</summary>
+    Task<Result> SaveBaselineAsync(DriftBaseline baseline, CancellationToken ct);
+
+    /// <summary>Retrieves the active baseline for a scope. Returns null value when none exists.</summary>
+    Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);
+
+    /// <summary>Lists all baselines, optionally filtered by scope.</summary>
+    Task<Result<IReadOnlyList<DriftBaseline>>> GetBaselinesAsync(DriftScope? scope, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftDetectionService.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftDetectionService.cs
new file mode 100644
index 0000000..870e4cb
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftDetectionService.cs
@@ -0,0 +1,23 @@
+using Domain.AI.DriftDetection;
+using Domain.Common;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Core service for evaluating agent quality drift against baselines.
+/// Orchestrates scoring, notification, and audit trail creation.
+/// </summary>
+public interface IDriftDetectionService
+{
+    /// <summary>Evaluates current dimension scores against the active baseline.</summary>
+    Task<Result<DriftScore>> EvaluateDriftAsync(DriftEvaluationRequest request, CancellationToken ct);
+
+    /// <summary>Retrieves the active baseline for a scope. Returns null value when no baseline exists.</summary>
+    Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);
+
+    /// <summary>Recalculates and persists a baseline from recent evaluation history.</summary>
+    Task<Result<DriftBaseline>> UpdateBaselineAsync(DriftBaselineUpdateRequest request, CancellationToken ct);
+
+    /// <summary>Retrieves historical drift scores within a time window.</summary>
+    Task<Result<IReadOnlyList<DriftScore>>> GetDriftHistoryAsync(DriftHistoryQuery query, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftNotificationChannel.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftNotificationChannel.cs
new file mode 100644
index 0000000..801473e
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftNotificationChannel.cs
@@ -0,0 +1,16 @@
+using Domain.AI.DriftDetection;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Individual notification channel for drift events (AG-UI SSE, logging, etc.).
+/// Multiple channels are registered and dispatched by <see cref="IDriftNotifier"/>.
+/// </summary>
+public interface IDriftNotificationChannel
+{
+    /// <summary>Notifies that drift has been detected above threshold.</summary>
+    Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct);
+
+    /// <summary>Notifies that a previously detected drift has been resolved.</summary>
+    Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftNotifier.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftNotifier.cs
new file mode 100644
index 0000000..1ee3cee
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftNotifier.cs
@@ -0,0 +1,16 @@
+using Domain.AI.DriftDetection;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Composite dispatcher that fans out drift notifications to all registered
+/// <see cref="IDriftNotificationChannel"/> instances. Consumed by <see cref="IDriftDetectionService"/>.
+/// </summary>
+public interface IDriftNotifier
+{
+    /// <summary>Dispatches drift-detected notification to all channels.</summary>
+    Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct);
+
+    /// <summary>Dispatches drift-resolved notification to all channels.</summary>
+    Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftScorer.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftScorer.cs
new file mode 100644
index 0000000..9e6ec4a
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftScorer.cs
@@ -0,0 +1,15 @@
+using Domain.AI.DriftDetection;
+using Domain.Common;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Scores a single dimension's current value against its baseline using a smoothing algorithm.
+/// Keyed DI: <c>"ewma"</c> (default).
+/// </summary>
+public interface IDriftScorer
+{
+    /// <summary>Computes a dimension score by comparing the current value against the baseline.</summary>
+    Task<Result<DriftDimensionScore>> ScoreDimensionAsync(
+        DriftDimension dimension, double currentValue, DriftBaseline baseline, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IEwmaStateStore.cs b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IEwmaStateStore.cs
new file mode 100644
index 0000000..1d601df
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IEwmaStateStore.cs
@@ -0,0 +1,20 @@
+using Domain.AI.DriftDetection;
+using Domain.Common;
+
+namespace Application.AI.Common.Interfaces.DriftDetection;
+
+/// <summary>
+/// Persistence contract for EWMA running state.
+/// Each scope+identifier+dimension combination has its own state entry.
+/// </summary>
+public interface IEwmaStateStore
+{
+    /// <summary>Retrieves EWMA state for a specific dimension. Returns null when not yet initialized.</summary>
+    Task<EwmaState?> GetStateAsync(DriftScope scope, string scopeIdentifier, DriftDimension dimension, CancellationToken ct);
+
+    /// <summary>Persists updated EWMA state.</summary>
+    Task<Result> SaveStateAsync(EwmaState state, CancellationToken ct);
+
+    /// <summary>Retrieves all EWMA states for a scope+identifier (all dimensions).</summary>
+    Task<IReadOnlyList<EwmaState>> GetStatesAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);
+}
diff --git a/src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoTests.cs b/src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoTests.cs
new file mode 100644
index 0000000..621277d
--- /dev/null
+++ b/src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoTests.cs
@@ -0,0 +1,108 @@
+using Application.AI.Common.Interfaces.DriftDetection;
+using Domain.AI.DriftDetection;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.AI.Common.Tests.Interfaces.DriftDetection;
+
+public sealed class DriftDetectionDtoTests
+{
+    [Fact]
+    public void DriftEvaluationRequest_ValidRequest_ConstructsSuccessfully()
+    {
+        var request = new DriftEvaluationRequest
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "code_review",
+            Dimensions = new Dictionary<DriftDimension, double>
+            {
+                [DriftDimension.Faithfulness] = 0.85,
+                [DriftDimension.Relevance] = 0.90
+            }.AsReadOnly()
+        };
+
+        request.Scope.Should().Be(DriftScope.Skill);
+        request.ScopeIdentifier.Should().Be("code_review");
+        request.Dimensions.Should().HaveCount(2);
+    }
+
+    [Fact]
+    public void DriftBaselineUpdateRequest_RequiresValidScope()
+    {
+        var request = new DriftBaselineUpdateRequest
+        {
+            Scope = DriftScope.Agent,
+            ScopeIdentifier = "primary_agent"
+        };
+
+        request.Scope.Should().Be(DriftScope.Agent);
+        request.ScopeIdentifier.Should().Be("primary_agent");
+    }
+
+    [Fact]
+    public void EwmaState_Construction_WithScopeDimensionAndInitialValues()
+    {
+        var state = new EwmaState
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "code_review",
+            Dimension = DriftDimension.Faithfulness,
+            CurrentEwma = 0.85,
+            SampleCount = 10,
+            LastUpdatedAt = DateTimeOffset.UtcNow
+        };
+
+        state.Scope.Should().Be(DriftScope.Skill);
+        state.ScopeIdentifier.Should().Be("code_review");
+        state.Dimension.Should().Be(DriftDimension.Faithfulness);
+        state.CurrentEwma.Should().Be(0.85);
+        state.SampleCount.Should().Be(10);
+    }
+
+    [Fact]
+    public void EwmaState_DeterministicId_MatchesExpectedPattern()
+    {
+        var state = new EwmaState
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "code_review",
+            Dimension = DriftDimension.Faithfulness,
+            CurrentEwma = 0.85,
+            SampleCount = 10,
+            LastUpdatedAt = DateTimeOffset.UtcNow
+        };
+
+        state.DeterministicId.Should().Be("ewma:Skill:code_review:Faithfulness");
+    }
+
+    [Fact]
+    public void DriftHistoryQuery_Construction_SetsAllProperties()
+    {
+        var start = DateTimeOffset.UtcNow.AddDays(-7);
+        var end = DateTimeOffset.UtcNow;
+
+        var query = new DriftHistoryQuery
+        {
+            Scope = DriftScope.TaskType,
+            ScopeIdentifier = "summarization",
+            Start = start,
+            End = end
+        };
+
+        query.Scope.Should().Be(DriftScope.TaskType);
+        query.ScopeIdentifier.Should().Be("summarization");
+        query.Start.Should().Be(start);
+        query.End.Should().Be(end);
+    }
+
+    [Fact]
+    public void DriftAuditQuery_OptionalFields_DefaultToNull()
+    {
+        var query = new DriftAuditQuery();
+
+        query.Start.Should().BeNull();
+        query.End.Should().BeNull();
+        query.RecordType.Should().BeNull();
+        query.EventId.Should().BeNull();
+    }
+}
diff --git a/src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoValidatorTests.cs b/src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoValidatorTests.cs
new file mode 100644
index 0000000..6d5282a
--- /dev/null
+++ b/src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoValidatorTests.cs
@@ -0,0 +1,155 @@
+using Application.AI.Common.Interfaces.DriftDetection;
+using Domain.AI.DriftDetection;
+using FluentValidation.TestHelper;
+using Xunit;
+
+namespace Application.AI.Common.Tests.Interfaces.DriftDetection;
+
+public sealed class DriftDetectionDtoValidatorTests
+{
+    private readonly DriftEvaluationRequestValidator _evalValidator = new();
+    private readonly DriftHistoryQueryValidator _historyValidator = new();
+    private readonly DriftAuditQueryValidator _auditValidator = new();
+    private readonly DriftBaselineUpdateRequestValidator _baselineValidator = new();
+
+    [Fact]
+    public void DriftEvaluationRequestValidator_EmptyDimensions_Fails()
+    {
+        var request = new DriftEvaluationRequest
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "code_review",
+            Dimensions = new Dictionary<DriftDimension, double>().AsReadOnly()
+        };
+
+        var result = _evalValidator.TestValidate(request);
+        result.ShouldHaveValidationErrorFor(x => x.Dimensions);
+    }
+
+    [Fact]
+    public void DriftEvaluationRequestValidator_EmptyScopeIdentifier_Fails()
+    {
+        var request = new DriftEvaluationRequest
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "",
+            Dimensions = new Dictionary<DriftDimension, double>
+            {
+                [DriftDimension.Faithfulness] = 0.85
+            }.AsReadOnly()
+        };
+
+        var result = _evalValidator.TestValidate(request);
+        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
+    }
+
+    [Fact]
+    public void DriftEvaluationRequestValidator_ValidRequest_Passes()
+    {
+        var request = new DriftEvaluationRequest
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "code_review",
+            Dimensions = new Dictionary<DriftDimension, double>
+            {
+                [DriftDimension.Faithfulness] = 0.85
+            }.AsReadOnly()
+        };
+
+        var result = _evalValidator.TestValidate(request);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void DriftHistoryQueryValidator_StartAfterEnd_Fails()
+    {
+        var query = new DriftHistoryQuery
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "code_review",
+            Start = DateTimeOffset.UtcNow,
+            End = DateTimeOffset.UtcNow.AddDays(-1)
+        };
+
+        var result = _historyValidator.TestValidate(query);
+        result.ShouldHaveValidationErrorFor(x => x.Start);
+    }
+
+    [Fact]
+    public void DriftHistoryQueryValidator_ValidRange_Passes()
+    {
+        var query = new DriftHistoryQuery
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "code_review",
+            Start = DateTimeOffset.UtcNow.AddDays(-7),
+            End = DateTimeOffset.UtcNow
+        };
+
+        var result = _historyValidator.TestValidate(query);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void DriftAuditQueryValidator_StartAfterEnd_Fails()
+    {
+        var query = new DriftAuditQuery
+        {
+            Start = DateTimeOffset.UtcNow,
+            End = DateTimeOffset.UtcNow.AddDays(-1)
+        };
+
+        var result = _auditValidator.TestValidate(query);
+        result.ShouldHaveValidationErrorFor(x => x.Start);
+    }
+
+    [Fact]
+    public void DriftAuditQueryValidator_BothNull_Passes()
+    {
+        var query = new DriftAuditQuery();
+
+        var result = _auditValidator.TestValidate(query);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void DriftBaselineUpdateRequestValidator_EmptyScopeIdentifier_Fails()
+    {
+        var request = new DriftBaselineUpdateRequest
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = ""
+        };
+
+        var result = _baselineValidator.TestValidate(request);
+        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
+    }
+
+    [Fact]
+    public void DriftBaselineUpdateRequestValidator_ValidRequest_Passes()
+    {
+        var request = new DriftBaselineUpdateRequest
+        {
+            Scope = DriftScope.Agent,
+            ScopeIdentifier = "primary_agent"
+        };
+
+        var result = _baselineValidator.TestValidate(request);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void DriftHistoryQueryValidator_EmptyScopeIdentifier_Fails()
+    {
+        var query = new DriftHistoryQuery
+        {
+            Scope = DriftScope.Skill,
+            ScopeIdentifier = "",
+            Start = DateTimeOffset.UtcNow.AddDays(-7),
+            End = DateTimeOffset.UtcNow
+        };
+
+        var result = _historyValidator.TestValidate(query);
+        result.ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
+    }
+}

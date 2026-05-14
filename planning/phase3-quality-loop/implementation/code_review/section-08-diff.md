diff --git a/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/DriftMetrics.cs b/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/DriftMetrics.cs
new file mode 100644
index 0000000..d5bd37c
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/DriftMetrics.cs
@@ -0,0 +1,27 @@
+using System.Diagnostics.Metrics;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common.Telemetry;
+
+namespace Application.AI.Common.OpenTelemetry.Metrics;
+
+/// <summary>
+/// OpenTelemetry metric instruments for drift detection.
+/// </summary>
+public static class DriftMetrics
+{
+    /// <summary>Drift evaluations completed. Tags: scope, severity.</summary>
+    public static Counter<long> Evaluations { get; } =
+        AppInstrument.Meter.CreateCounter<long>(DriftConventions.Evaluations, "{evaluation}", "Drift evaluations completed");
+
+    /// <summary>Drift-triggered escalations.</summary>
+    public static Counter<long> EscalationsTriggered { get; } =
+        AppInstrument.Meter.CreateCounter<long>(DriftConventions.EscalationsTriggered, "{escalation}", "Drift-triggered escalations");
+
+    /// <summary>Drift baselines updated.</summary>
+    public static Counter<long> BaselinesUpdated { get; } =
+        AppInstrument.Meter.CreateCounter<long>(DriftConventions.BaselinesUpdated, "{baseline}", "Drift baselines updated");
+
+    /// <summary>Drift evaluation duration in milliseconds.</summary>
+    public static Histogram<double> EvaluationDurationMs { get; } =
+        AppInstrument.Meter.CreateHistogram<double>(DriftConventions.EvaluationDurationMs, "ms", "Drift evaluation duration");
+}
diff --git a/src/Content/Domain/Domain.AI/Telemetry/Conventions/DriftConventions.cs b/src/Content/Domain/Domain.AI/Telemetry/Conventions/DriftConventions.cs
new file mode 100644
index 0000000..801d2af
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/Telemetry/Conventions/DriftConventions.cs
@@ -0,0 +1,34 @@
+namespace Domain.AI.Telemetry.Conventions;
+
+/// <summary>
+/// OpenTelemetry semantic conventions for drift detection metrics and traces.
+/// </summary>
+public static class DriftConventions
+{
+    public const string Scope = "agent.drift.scope";
+    public const string ScopeIdentifier = "agent.drift.scope_identifier";
+    public const string Severity = "agent.drift.severity";
+    public const string Dimension = "agent.drift.dimension";
+
+    public const string Evaluations = "agent.drift.evaluations";
+    public const string EscalationsTriggered = "agent.drift.escalations_triggered";
+    public const string BaselinesUpdated = "agent.drift.baselines_updated";
+    public const string EvaluationDurationMs = "agent.drift.evaluation_duration_ms";
+
+    /// <summary>Well-known tag values for <see cref="Severity"/>.</summary>
+    public static class SeverityValues
+    {
+        public const string None = "none";
+        public const string Warn = "warn";
+        public const string Alert = "alert";
+        public const string Escalate = "escalate";
+    }
+
+    /// <summary>Well-known tag values for <see cref="Scope"/>.</summary>
+    public static class ScopeValues
+    {
+        public const string Agent = "agent";
+        public const string Skill = "skill";
+        public const string TaskType = "task_type";
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/CompositeDriftNotifier.cs b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/CompositeDriftNotifier.cs
new file mode 100644
index 0000000..21946e6
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/CompositeDriftNotifier.cs
@@ -0,0 +1,51 @@
+using Application.AI.Common.Interfaces.DriftDetection;
+using Domain.AI.DriftDetection;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.DriftDetection;
+
+/// <summary>
+/// Fans out drift notifications to all registered <see cref="IDriftNotificationChannel"/> implementations.
+/// Individual channel failures are logged but do not block other channels.
+/// </summary>
+public sealed class CompositeDriftNotifier : IDriftNotifier
+{
+    private readonly IReadOnlyList<IDriftNotificationChannel> _channels;
+    private readonly ILogger<CompositeDriftNotifier> _logger;
+
+    public CompositeDriftNotifier(
+        IEnumerable<IDriftNotificationChannel> channels,
+        ILogger<CompositeDriftNotifier> logger)
+    {
+        _channels = channels.ToList();
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct) =>
+        FanOutAsync(channel => channel.NotifyDriftDetectedAsync(score, ct));
+
+    /// <inheritdoc />
+    public Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct) =>
+        FanOutAsync(channel => channel.NotifyDriftResolvedAsync(driftEvent, ct));
+
+    private Task FanOutAsync(Func<IDriftNotificationChannel, Task> action)
+    {
+        var tasks = _channels.Select(channel => SafeNotifyAsync(action, channel));
+        return Task.WhenAll(tasks);
+    }
+
+    private async Task SafeNotifyAsync(
+        Func<IDriftNotificationChannel, Task> action,
+        IDriftNotificationChannel channel)
+    {
+        try
+        {
+            await action(channel);
+        }
+        catch (Exception ex)
+        {
+            _logger.LogWarning(ex, "Drift notification channel {Channel} failed", channel.GetType().Name);
+        }
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs
new file mode 100644
index 0000000..2bb6881
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs
@@ -0,0 +1,363 @@
+using System.Globalization;
+using System.Text.Json;
+using Application.AI.Common.Interfaces.DriftDetection;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.KnowledgeGraph;
+using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.DriftDetection;
+using Domain.AI.Escalation;
+using Domain.AI.KnowledgeGraph.Models;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common;
+using Domain.Common.Config;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.DriftDetection;
+
+/// <summary>
+/// Orchestrates the full drift evaluation pipeline: baseline resolution with scope fallback,
+/// per-dimension EWMA scoring, severity classification, escalation triggering,
+/// audit recording, graph persistence, and notification dispatch.
+/// </summary>
+public sealed class DefaultDriftDetectionService : IDriftDetectionService
+{
+    private static readonly DriftScope[] FallbackOrder = [DriftScope.TaskType, DriftScope.Skill, DriftScope.Agent];
+
+    private readonly IDriftScorer _scorer;
+    private readonly IDriftBaselineStore _baselineStore;
+    private readonly IDriftAuditStore _auditStore;
+    private readonly IDriftNotifier _notifier;
+    private readonly IEscalationService _escalationService;
+    private readonly IKnowledgeGraphStore _graphStore;
+    private readonly IOptionsMonitor<AppConfig> _options;
+    private readonly TimeProvider _timeProvider;
+    private readonly ILogger<DefaultDriftDetectionService> _logger;
+
+    public DefaultDriftDetectionService(
+        IDriftScorer scorer,
+        IDriftBaselineStore baselineStore,
+        IDriftAuditStore auditStore,
+        IDriftNotifier notifier,
+        IEscalationService escalationService,
+        IKnowledgeGraphStore graphStore,
+        IOptionsMonitor<AppConfig> options,
+        TimeProvider timeProvider,
+        ILogger<DefaultDriftDetectionService> logger)
+    {
+        _scorer = scorer;
+        _baselineStore = baselineStore;
+        _auditStore = auditStore;
+        _notifier = notifier;
+        _escalationService = escalationService;
+        _graphStore = graphStore;
+        _options = options;
+        _timeProvider = timeProvider;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task<Result<DriftScore>> EvaluateDriftAsync(DriftEvaluationRequest request, CancellationToken ct)
+    {
+        var config = _options.CurrentValue.AI.DriftDetection;
+        var now = _timeProvider.GetUtcNow();
+
+        if (!config.Enabled)
+        {
+            return Result<DriftScore>.Success(new DriftScore
+            {
+                ScoreId = Guid.NewGuid(),
+                BaselineId = Guid.Empty,
+                Scope = request.Scope,
+                ScopeIdentifier = request.ScopeIdentifier,
+                Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>().AsReadOnly(),
+                OverallDrift = 0.0,
+                Severity = DriftSeverity.None,
+                ScoredAt = now
+            });
+        }
+
+        var baseline = await ResolveBaselineWithFallbackAsync(request.Scope, request.ScopeIdentifier, ct);
+        if (baseline is null)
+            return Result<DriftScore>.Fail($"No baseline available for scope {request.Scope}:{request.ScopeIdentifier}");
+
+        var dimensionScores = new Dictionary<DriftDimension, DriftDimensionScore>();
+        foreach (var (dimension, currentValue) in request.Dimensions)
+        {
+            var scoreResult = await _scorer.ScoreDimensionAsync(dimension, currentValue, baseline, ct);
+            if (scoreResult.IsSuccess)
+                dimensionScores[dimension] = scoreResult.Value!;
+            else
+                _logger.LogWarning("Scorer failed for {Dimension}: {Errors}", dimension, string.Join(", ", scoreResult.Errors));
+        }
+
+        if (dimensionScores.Count == 0)
+            return Result<DriftScore>.Fail("All dimensions failed scoring");
+
+        var overallDrift = dimensionScores.Values.Max(d => d.Deviation);
+        var severity = DriftSeverityClassifier.Classify(overallDrift, config);
+
+        var score = new DriftScore
+        {
+            ScoreId = Guid.NewGuid(),
+            BaselineId = baseline.BaselineId,
+            Scope = request.Scope,
+            ScopeIdentifier = request.ScopeIdentifier,
+            Dimensions = dimensionScores.AsReadOnly(),
+            OverallDrift = overallDrift,
+            Severity = severity,
+            ScoredAt = now
+        };
+
+        if (severity >= DriftSeverity.Warn)
+        {
+            await SafeExecuteAsync("graph persistence",
+                () => PersistDriftEventAsync(score, now, ct));
+
+            if (severity == DriftSeverity.Escalate && config.EscalationEnabled)
+                await SafeExecuteAsync("escalation",
+                    () => TriggerEscalationAsync(score, now, ct));
+
+            await SafeExecuteAsync("notification",
+                () => _notifier.NotifyDriftDetectedAsync(score, ct));
+        }
+
+        await SafeExecuteAsync("audit",
+            () => RecordAuditAsync(score, DriftAuditRecordType.Detected, now, ct));
+
+        DriftMetrics.Evaluations.Add(1,
+            new(DriftConventions.Scope, score.Scope.ToString().ToLowerInvariant()),
+            new(DriftConventions.Severity, severity.ToString().ToLowerInvariant()));
+
+        return Result<DriftScore>.Success(score);
+    }
+
+    /// <inheritdoc />
+    public async Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct) =>
+        await _baselineStore.GetBaselineAsync(scope, scopeIdentifier, ct);
+
+    /// <inheritdoc />
+    public async Task<Result<DriftBaseline>> UpdateBaselineAsync(DriftBaselineUpdateRequest request, CancellationToken ct)
+    {
+        var config = _options.CurrentValue.AI.DriftDetection;
+        var now = _timeProvider.GetUtcNow();
+
+        if (!config.Enabled)
+            return Result<DriftBaseline>.Fail("Drift detection is disabled");
+
+        var historyQuery = new DriftHistoryQuery
+        {
+            Scope = request.Scope,
+            ScopeIdentifier = request.ScopeIdentifier,
+            Start = now.AddDays(-config.BaselineWindowDays),
+            End = now
+        };
+
+        var historyResult = await GetDriftHistoryAsync(historyQuery, ct);
+        if (!historyResult.IsSuccess)
+            return Result<DriftBaseline>.Fail(historyResult.Errors.ToArray());
+
+        var scores = historyResult.Value!;
+        if (scores.Count < config.MinSamplesForBaseline)
+            return Result<DriftBaseline>.Fail(
+                $"Insufficient samples: {scores.Count}/{config.MinSamplesForBaseline}");
+
+        var dimensionMeans = new Dictionary<DriftDimension, double>();
+        var dimensionSigmas = new Dictionary<DriftDimension, double>();
+
+        var allDimensions = scores
+            .SelectMany(s => s.Dimensions.Keys)
+            .Distinct();
+
+        foreach (var dim in allDimensions)
+        {
+            var values = scores
+                .Where(s => s.Dimensions.ContainsKey(dim))
+                .Select(s => s.Dimensions[dim].CurrentValue)
+                .ToList();
+
+            if (values.Count == 0) continue;
+
+            var mean = values.Average();
+            var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
+            dimensionMeans[dim] = mean;
+            dimensionSigmas[dim] = Math.Sqrt(variance);
+        }
+
+        var baseline = new DriftBaseline
+        {
+            BaselineId = Guid.NewGuid(),
+            Scope = request.Scope,
+            ScopeIdentifier = request.ScopeIdentifier,
+            Dimensions = dimensionMeans.AsReadOnly(),
+            DimensionSigmas = dimensionSigmas.AsReadOnly(),
+            SampleCount = scores.Count,
+            WindowStart = historyQuery.Start,
+            WindowEnd = historyQuery.End,
+            CreatedAt = now
+        };
+
+        var saveResult = await _baselineStore.SaveBaselineAsync(baseline, ct);
+        if (!saveResult.IsSuccess)
+            return Result<DriftBaseline>.Fail(saveResult.Errors.ToArray());
+
+        await SafeExecuteAsync("audit",
+            () => RecordAuditAsync(null, DriftAuditRecordType.BaselineUpdated, now, ct));
+
+        DriftMetrics.BaselinesUpdated.Add(1);
+        return Result<DriftBaseline>.Success(baseline);
+    }
+
+    /// <inheritdoc />
+    public async Task<Result<IReadOnlyList<DriftScore>>> GetDriftHistoryAsync(DriftHistoryQuery query, CancellationToken ct)
+    {
+        try
+        {
+            var allNodes = await _graphStore.GetAllNodesAsync(ct);
+            var driftScores = allNodes
+                .Where(n => n.Type == "DriftEvent")
+                .Where(n => n.Properties.TryGetValue("Scope", out var s) && s == query.Scope.ToString())
+                .Where(n => n.Properties.TryGetValue("ScopeIdentifier", out var id) && id == query.ScopeIdentifier)
+                .Where(n =>
+                {
+                    if (!n.Properties.TryGetValue("ScoredAt", out var ts)) return false;
+                    if (!DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, out var scored)) return false;
+                    return scored >= query.Start && scored <= query.End;
+                })
+                .Select(n => DeserializeDriftScore(n))
+                .Where(s => s is not null)
+                .Cast<DriftScore>()
+                .ToList();
+
+            return Result<IReadOnlyList<DriftScore>>.Success(driftScores.AsReadOnly());
+        }
+        catch (Exception ex)
+        {
+            _logger.LogError(ex, "Failed to query drift history");
+            return Result<IReadOnlyList<DriftScore>>.Fail($"Failed to query drift history: {ex.Message}");
+        }
+    }
+
+    private async Task<DriftBaseline?> ResolveBaselineWithFallbackAsync(
+        DriftScope requestedScope, string scopeIdentifier, CancellationToken ct)
+    {
+        var startIndex = Array.IndexOf(FallbackOrder, requestedScope);
+        if (startIndex < 0) startIndex = 0;
+
+        for (var i = startIndex; i < FallbackOrder.Length; i++)
+        {
+            var scope = FallbackOrder[i];
+            var result = await _baselineStore.GetBaselineAsync(scope, scopeIdentifier, ct);
+            if (result.IsSuccess && result.Value is not null)
+                return result.Value;
+        }
+
+        return null;
+    }
+
+    private async Task PersistDriftEventAsync(DriftScore score, DateTimeOffset now, CancellationToken ct)
+    {
+        var eventId = Guid.NewGuid();
+        var node = new GraphNode
+        {
+            Id = $"driftevent:{eventId}",
+            Name = $"DriftEvent:{score.Scope}:{score.ScopeIdentifier}",
+            Type = "DriftEvent",
+            Properties = new Dictionary<string, string>
+            {
+                ["EventId"] = eventId.ToString(),
+                ["ScoreId"] = score.ScoreId.ToString(),
+                ["Scope"] = score.Scope.ToString(),
+                ["ScopeIdentifier"] = score.ScopeIdentifier,
+                ["Severity"] = score.Severity.ToString(),
+                ["OverallDrift"] = score.OverallDrift.ToString(CultureInfo.InvariantCulture),
+                ["ScoredAt"] = score.ScoredAt.ToString("o"),
+                ["DimensionsJson"] = JsonSerializer.Serialize(score.Dimensions)
+            }.AsReadOnly(),
+            CreatedAt = now
+        };
+
+        await _graphStore.AddNodesAsync([node], ct);
+    }
+
+    private async Task TriggerEscalationAsync(DriftScore score, DateTimeOffset now, CancellationToken ct)
+    {
+        var driftedDimensions = score.Dimensions
+            .Where(d => d.Value.Deviation >= _options.CurrentValue.AI.DriftDetection.WarnThresholdSigma)
+            .Select(d => $"{d.Key}: {d.Value.Deviation:F2}σ");
+
+        var request = new EscalationRequest
+        {
+            EscalationId = Guid.NewGuid(),
+            AgentId = score.ScopeIdentifier,
+            ToolName = "drift_detection",
+            Arguments = new Dictionary<string, string>
+            {
+                ["score_id"] = score.ScoreId.ToString(),
+                ["severity"] = score.Severity.ToString()
+            }.AsReadOnly(),
+            Description = $"Drift detected in {score.Scope}:{score.ScopeIdentifier} — " +
+                          $"overall {score.OverallDrift:F2}σ. Dimensions: {string.Join(", ", driftedDimensions)}",
+            RiskLevel = RiskLevel.High,
+            Priority = EscalationPriority.Blocking,
+            ApprovalStrategy = ApprovalStrategyType.AnyOf,
+            Approvers = [],
+            RequestedAt = now
+        };
+
+        await _escalationService.QueueEscalationAsync(request, ct);
+        DriftMetrics.EscalationsTriggered.Add(1);
+    }
+
+    private async Task RecordAuditAsync(DriftScore? score, DriftAuditRecordType recordType, DateTimeOffset now, CancellationToken ct)
+    {
+        var record = new DriftAuditRecord
+        {
+            RecordId = Guid.NewGuid(),
+            EventId = score?.ScoreId ?? Guid.Empty,
+            RecordType = recordType,
+            Payload = score is not null ? JsonSerializer.Serialize(score) : "{}",
+            RecordedAt = now
+        };
+
+        await _auditStore.RecordAsync(record, ct);
+    }
+
+    private async Task SafeExecuteAsync(string operation, Func<Task> action)
+    {
+        try
+        {
+            await action();
+        }
+        catch (Exception ex)
+        {
+            _logger.LogError(ex, "Non-critical operation '{Operation}' failed during drift evaluation", operation);
+        }
+    }
+
+    private static DriftScore? DeserializeDriftScore(GraphNode node)
+    {
+        try
+        {
+            var dimensions = node.Properties.TryGetValue("DimensionsJson", out var json)
+                ? JsonSerializer.Deserialize<Dictionary<DriftDimension, DriftDimensionScore>>(json) ?? []
+                : [];
+
+            return new DriftScore
+            {
+                ScoreId = Guid.Parse(node.Properties.GetValueOrDefault("ScoreId", Guid.Empty.ToString())),
+                BaselineId = Guid.Empty,
+                Scope = Enum.Parse<DriftScope>(node.Properties["Scope"]),
+                ScopeIdentifier = node.Properties["ScopeIdentifier"],
+                Dimensions = dimensions.AsReadOnly(),
+                OverallDrift = double.Parse(node.Properties.GetValueOrDefault("OverallDrift", "0"), CultureInfo.InvariantCulture),
+                Severity = Enum.Parse<DriftSeverity>(node.Properties["Severity"]),
+                ScoredAt = DateTimeOffset.Parse(node.Properties["ScoredAt"], CultureInfo.InvariantCulture)
+            };
+        }
+        catch
+        {
+            return null;
+        }
+    }
+}
diff --git a/src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/DriftMetricsTests.cs b/src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/DriftMetricsTests.cs
new file mode 100644
index 0000000..1cdb642
--- /dev/null
+++ b/src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/DriftMetricsTests.cs
@@ -0,0 +1,24 @@
+using Application.AI.Common.OpenTelemetry.Metrics;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.AI.Common.Tests.OpenTelemetry.Metrics;
+
+public sealed class DriftMetricsTests
+{
+    [Fact]
+    public void Evaluations_Counter_IsNotNull() =>
+        DriftMetrics.Evaluations.Should().NotBeNull();
+
+    [Fact]
+    public void EscalationsTriggered_Counter_IsNotNull() =>
+        DriftMetrics.EscalationsTriggered.Should().NotBeNull();
+
+    [Fact]
+    public void BaselinesUpdated_Counter_IsNotNull() =>
+        DriftMetrics.BaselinesUpdated.Should().NotBeNull();
+
+    [Fact]
+    public void EvaluationDurationMs_Histogram_IsNotNull() =>
+        DriftMetrics.EvaluationDurationMs.Should().NotBeNull();
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/CompositeDriftNotifierTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/CompositeDriftNotifierTests.cs
new file mode 100644
index 0000000..eb492f6
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/CompositeDriftNotifierTests.cs
@@ -0,0 +1,115 @@
+using Application.AI.Common.Interfaces.DriftDetection;
+using Domain.AI.DriftDetection;
+using FluentAssertions;
+using Infrastructure.AI.DriftDetection;
+using Microsoft.Extensions.Logging;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.DriftDetection;
+
+public sealed class CompositeDriftNotifierTests
+{
+    private readonly Mock<ILogger<CompositeDriftNotifier>> _loggerMock = new();
+
+    private static DriftScore CreateTestScore() => new()
+    {
+        ScoreId = Guid.NewGuid(),
+        BaselineId = Guid.NewGuid(),
+        Scope = DriftScope.Skill,
+        ScopeIdentifier = "code_review",
+        Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>
+        {
+            [DriftDimension.Faithfulness] = new()
+            {
+                CurrentValue = 0.7,
+                BaselineValue = 0.8,
+                EwmaValue = 0.75,
+                Deviation = 0.5
+            }
+        }.AsReadOnly(),
+        OverallDrift = 0.5,
+        Severity = DriftSeverity.None,
+        ScoredAt = DateTimeOffset.UtcNow
+    };
+
+    private static DriftEvent CreateTestEvent() => new()
+    {
+        EventId = Guid.NewGuid(),
+        DriftScore = CreateTestScore(),
+        DetectedAt = DateTimeOffset.UtcNow
+    };
+
+    [Fact]
+    public async Task NotifyDriftDetected_FansOutToAllChannels()
+    {
+        // Arrange
+        var channel1 = new Mock<IDriftNotificationChannel>();
+        var channel2 = new Mock<IDriftNotificationChannel>();
+        var channel3 = new Mock<IDriftNotificationChannel>();
+        var notifier = new CompositeDriftNotifier(
+            [channel1.Object, channel2.Object, channel3.Object], _loggerMock.Object);
+        var score = CreateTestScore();
+
+        // Act
+        await notifier.NotifyDriftDetectedAsync(score, CancellationToken.None);
+
+        // Assert
+        channel1.Verify(c => c.NotifyDriftDetectedAsync(score, It.IsAny<CancellationToken>()), Times.Once);
+        channel2.Verify(c => c.NotifyDriftDetectedAsync(score, It.IsAny<CancellationToken>()), Times.Once);
+        channel3.Verify(c => c.NotifyDriftDetectedAsync(score, It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task NotifyDriftResolved_FansOutToAllChannels()
+    {
+        // Arrange
+        var channel1 = new Mock<IDriftNotificationChannel>();
+        var channel2 = new Mock<IDriftNotificationChannel>();
+        var notifier = new CompositeDriftNotifier(
+            [channel1.Object, channel2.Object], _loggerMock.Object);
+        var driftEvent = CreateTestEvent();
+
+        // Act
+        await notifier.NotifyDriftResolvedAsync(driftEvent, CancellationToken.None);
+
+        // Assert
+        channel1.Verify(c => c.NotifyDriftResolvedAsync(driftEvent, It.IsAny<CancellationToken>()), Times.Once);
+        channel2.Verify(c => c.NotifyDriftResolvedAsync(driftEvent, It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task ChannelFailure_LogsWarning_DoesNotBlockOtherChannels()
+    {
+        // Arrange
+        var failingChannel = new Mock<IDriftNotificationChannel>();
+        failingChannel
+            .Setup(c => c.NotifyDriftDetectedAsync(It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("Channel down"));
+
+        var successChannel = new Mock<IDriftNotificationChannel>();
+        var notifier = new CompositeDriftNotifier(
+            [failingChannel.Object, successChannel.Object], _loggerMock.Object);
+
+        // Act
+        var act = () => notifier.NotifyDriftDetectedAsync(CreateTestScore(), CancellationToken.None);
+
+        // Assert — should not throw
+        await act.Should().NotThrowAsync();
+        successChannel.Verify(c => c.NotifyDriftDetectedAsync(
+            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task NoChannels_CompletesWithoutError()
+    {
+        // Arrange
+        var notifier = new CompositeDriftNotifier([], _loggerMock.Object);
+
+        // Act
+        var act = () => notifier.NotifyDriftDetectedAsync(CreateTestScore(), CancellationToken.None);
+
+        // Assert
+        await act.Should().NotThrowAsync();
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs
new file mode 100644
index 0000000..f72c9ee
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs
@@ -0,0 +1,385 @@
+using Application.AI.Common.Interfaces.DriftDetection;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.KnowledgeGraph;
+using Domain.AI.DriftDetection;
+using Domain.AI.Escalation;
+using Domain.AI.KnowledgeGraph.Models;
+using Domain.Common;
+using Domain.Common.Config;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.DriftDetection;
+using FluentAssertions;
+using Infrastructure.AI.DriftDetection;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Microsoft.Extensions.Time.Testing;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.DriftDetection;
+
+public sealed class DefaultDriftDetectionServiceTests
+{
+    private readonly Mock<IDriftScorer> _scorerMock = new();
+    private readonly Mock<IDriftBaselineStore> _baselineStoreMock = new();
+    private readonly Mock<IDriftAuditStore> _auditStoreMock = new();
+    private readonly Mock<IDriftNotifier> _notifierMock = new();
+    private readonly Mock<IEscalationService> _escalationMock = new();
+    private readonly Mock<IKnowledgeGraphStore> _graphStoreMock = new();
+    private readonly Mock<ILogger<DefaultDriftDetectionService>> _loggerMock = new();
+    private readonly FakeTimeProvider _timeProvider = new();
+
+    private DefaultDriftDetectionService CreateService(
+        bool enabled = true, bool escalationEnabled = true,
+        double warnThreshold = 1.5, double alertThreshold = 2.5, double escalateThreshold = 3.0,
+        int minSamples = 20, int baselineWindowDays = 7)
+    {
+        var appConfig = new AppConfig
+        {
+            AI = new AIConfig
+            {
+                DriftDetection = new DriftDetectionConfig
+                {
+                    Enabled = enabled,
+                    EscalationEnabled = escalationEnabled,
+                    EwmaLambda = 0.2,
+                    ControlLimitWidth = 3.0,
+                    WarnThresholdSigma = warnThreshold,
+                    AlertThresholdSigma = alertThreshold,
+                    EscalateThresholdSigma = escalateThreshold,
+                    MinSamplesForBaseline = minSamples,
+                    BaselineWindowDays = baselineWindowDays
+                }
+            }
+        };
+
+        var optionsMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(
+            o => o.CurrentValue == appConfig);
+
+        return new DefaultDriftDetectionService(
+            _scorerMock.Object,
+            _baselineStoreMock.Object,
+            _auditStoreMock.Object,
+            _notifierMock.Object,
+            _escalationMock.Object,
+            _graphStoreMock.Object,
+            optionsMonitor,
+            _timeProvider,
+            _loggerMock.Object);
+    }
+
+    private static DriftBaseline CreateTestBaseline(
+        DriftScope scope = DriftScope.Skill, string identifier = "code_review",
+        Dictionary<DriftDimension, double>? means = null,
+        Dictionary<DriftDimension, double>? sigmas = null)
+    {
+        means ??= new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.8,
+            [DriftDimension.Relevance] = 0.85,
+            [DriftDimension.Coherence] = 0.9
+        };
+        sigmas ??= means.ToDictionary(kv => kv.Key, _ => 0.1);
+
+        return new DriftBaseline
+        {
+            BaselineId = Guid.NewGuid(),
+            Scope = scope,
+            ScopeIdentifier = identifier,
+            Dimensions = means.AsReadOnly(),
+            DimensionSigmas = sigmas.AsReadOnly(),
+            SampleCount = 30,
+            WindowStart = DateTimeOffset.UtcNow.AddDays(-7),
+            WindowEnd = DateTimeOffset.UtcNow,
+            CreatedAt = DateTimeOffset.UtcNow
+        };
+    }
+
+    private static DriftEvaluationRequest CreateTestRequest(
+        DriftScope scope = DriftScope.Skill, string identifier = "code_review",
+        Dictionary<DriftDimension, double>? dimensions = null)
+    {
+        dimensions ??= new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.75,
+            [DriftDimension.Relevance] = 0.8,
+            [DriftDimension.Coherence] = 0.85
+        };
+
+        return new DriftEvaluationRequest
+        {
+            Scope = scope,
+            ScopeIdentifier = identifier,
+            Dimensions = dimensions.AsReadOnly()
+        };
+    }
+
+    private void SetupBaselineReturn(DriftScope scope, string identifier, DriftBaseline? baseline)
+    {
+        _baselineStoreMock
+            .Setup(b => b.GetBaselineAsync(scope, identifier, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result<DriftBaseline?>.Success(baseline));
+    }
+
+    private void SetupScorerReturns(double deviation)
+    {
+        _scorerMock
+            .Setup(s => s.ScoreDimensionAsync(
+                It.IsAny<DriftDimension>(), It.IsAny<double>(),
+                It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((DriftDimension _, double currentValue, DriftBaseline baseline, CancellationToken _) =>
+                Result<DriftDimensionScore>.Success(new DriftDimensionScore
+                {
+                    CurrentValue = currentValue,
+                    BaselineValue = 0.8,
+                    EwmaValue = 0.75,
+                    Deviation = deviation
+                }));
+    }
+
+    private void SetupAuditSuccess()
+    {
+        _auditStoreMock
+            .Setup(a => a.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result.Success());
+    }
+
+    private void SetupGraphSuccess()
+    {
+        _graphStoreMock
+            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+        _graphStoreMock
+            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+    }
+
+    // ===== EvaluateDriftAsync Tests =====
+
+    [Fact]
+    public async Task EvaluateDrift_WithBaseline_ScoresAllDimensions()
+    {
+        var baseline = CreateTestBaseline();
+        SetupBaselineReturn(DriftScope.Skill, "code_review", baseline);
+        SetupScorerReturns(deviation: 1.0);
+        SetupAuditSuccess();
+        var service = CreateService();
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Dimensions.Should().HaveCount(3);
+        _scorerMock.Verify(s => s.ScoreDimensionAsync(
+            It.IsAny<DriftDimension>(), It.IsAny<double>(),
+            baseline, It.IsAny<CancellationToken>()), Times.Exactly(3));
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_NoBaseline_ReturnsFailure()
+    {
+        SetupBaselineReturn(DriftScope.Skill, "code_review", null);
+        SetupBaselineReturn(DriftScope.Agent, "code_review", null);
+        var service = CreateService();
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeFalse();
+        result.Errors.Should().Contain(e => e.Contains("No baseline"));
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_BaselineFallback_SkillToAgent()
+    {
+        var agentBaseline = CreateTestBaseline(DriftScope.Agent, "code_review");
+        SetupBaselineReturn(DriftScope.Skill, "code_review", null);
+        SetupBaselineReturn(DriftScope.Agent, "code_review", agentBaseline);
+        SetupScorerReturns(deviation: 1.0);
+        SetupAuditSuccess();
+        var service = CreateService();
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        _baselineStoreMock.Verify(b => b.GetBaselineAsync(DriftScope.Skill, "code_review", It.IsAny<CancellationToken>()), Times.Once);
+        _baselineStoreMock.Verify(b => b.GetBaselineAsync(DriftScope.Agent, "code_review", It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_SeverityWarn_EmitsNotification_NoEscalation()
+    {
+        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
+        SetupScorerReturns(deviation: 2.0);
+        SetupAuditSuccess();
+        SetupGraphSuccess();
+        var service = CreateService();
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Severity.Should().Be(DriftSeverity.Warn);
+        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
+            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
+        _escalationMock.Verify(e => e.QueueEscalationAsync(
+            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_SeverityEscalate_TriggersEscalationService()
+    {
+        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
+        SetupScorerReturns(deviation: 3.5);
+        SetupAuditSuccess();
+        SetupGraphSuccess();
+        _escalationMock
+            .Setup(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Guid.NewGuid());
+        var service = CreateService();
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Severity.Should().Be(DriftSeverity.Escalate);
+        _escalationMock.Verify(e => e.QueueEscalationAsync(
+            It.Is<EscalationRequest>(r =>
+                r.ToolName == "drift_detection" &&
+                r.RiskLevel == RiskLevel.High),
+            It.IsAny<CancellationToken>()), Times.Once);
+        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
+            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_SeverityEscalate_EscalationDisabled_SkipsEscalation()
+    {
+        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
+        SetupScorerReturns(deviation: 3.5);
+        SetupAuditSuccess();
+        SetupGraphSuccess();
+        var service = CreateService(escalationEnabled: false);
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Severity.Should().Be(DriftSeverity.Escalate);
+        _escalationMock.Verify(e => e.QueueEscalationAsync(
+            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
+        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
+            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_RecordsAuditEntry()
+    {
+        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
+        SetupScorerReturns(deviation: 2.0);
+        SetupAuditSuccess();
+        SetupGraphSuccess();
+        var service = CreateService();
+
+        await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        _auditStoreMock.Verify(a => a.RecordAsync(
+            It.Is<DriftAuditRecord>(r => r.RecordType == DriftAuditRecordType.Detected),
+            It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_SeverityWarn_CreatesGraphNode()
+    {
+        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
+        SetupScorerReturns(deviation: 2.0);
+        SetupAuditSuccess();
+        SetupGraphSuccess();
+        var service = CreateService();
+
+        await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        _graphStoreMock.Verify(g => g.AddNodesAsync(
+            It.Is<IReadOnlyList<GraphNode>>(nodes =>
+                nodes.Count == 1 && nodes[0].Type == "DriftEvent"),
+            It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_OverallDrift_IsMaxDeviation()
+    {
+        var baseline = CreateTestBaseline();
+        SetupBaselineReturn(DriftScope.Skill, "code_review", baseline);
+        SetupAuditSuccess();
+
+        var callCount = 0;
+        double[] deviations = [1.0, 2.5, 1.8];
+        _scorerMock
+            .Setup(s => s.ScoreDimensionAsync(
+                It.IsAny<DriftDimension>(), It.IsAny<double>(),
+                It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() =>
+            {
+                var idx = Interlocked.Increment(ref callCount) - 1;
+                var dev = deviations[idx % deviations.Length];
+                return Result<DriftDimensionScore>.Success(new DriftDimensionScore
+                {
+                    CurrentValue = 0.7,
+                    BaselineValue = 0.8,
+                    EwmaValue = 0.75,
+                    Deviation = dev
+                });
+            });
+        SetupGraphSuccess();
+
+        var service = CreateService();
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.OverallDrift.Should().Be(2.5);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_Disabled_ReturnsSuccessNoOp()
+    {
+        var service = CreateService(enabled: false);
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Severity.Should().Be(DriftSeverity.None);
+        _scorerMock.Verify(s => s.ScoreDimensionAsync(
+            It.IsAny<DriftDimension>(), It.IsAny<double>(),
+            It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()), Times.Never);
+        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
+            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Never);
+    }
+
+    [Fact]
+    public async Task EvaluateDrift_SeverityNone_NoNotification_NoGraphNode()
+    {
+        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
+        SetupScorerReturns(deviation: 0.5);
+        SetupAuditSuccess();
+        var service = CreateService();
+
+        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Severity.Should().Be(DriftSeverity.None);
+        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
+            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Never);
+        _graphStoreMock.Verify(g => g.AddNodesAsync(
+            It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()), Times.Never);
+    }
+
+    // ===== GetBaselineAsync =====
+
+    [Fact]
+    public async Task GetBaseline_DelegatesToStore()
+    {
+        var baseline = CreateTestBaseline();
+        SetupBaselineReturn(DriftScope.Skill, "code_review", baseline);
+        var service = CreateService();
+
+        var result = await service.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().Be(baseline);
+    }
+}

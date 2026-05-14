using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.DriftDetection;
using Domain.AI.Escalation;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Orchestrates the full drift evaluation pipeline: baseline resolution with scope fallback,
/// per-dimension EWMA scoring, severity classification, escalation triggering,
/// audit recording, graph persistence, and notification dispatch.
/// </summary>
public sealed class DefaultDriftDetectionService : IDriftDetectionService
{
    private static readonly DriftScope[] FallbackOrder = [DriftScope.TaskType, DriftScope.Skill, DriftScope.Agent];

    private readonly IDriftScorer _scorer;
    private readonly IDriftBaselineStore _baselineStore;
    private readonly IDriftAuditStore _auditStore;
    private readonly IDriftNotifier _notifier;
    private readonly IEscalationService _escalationService;
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DefaultDriftDetectionService> _logger;

    public DefaultDriftDetectionService(
        IDriftScorer scorer,
        IDriftBaselineStore baselineStore,
        IDriftAuditStore auditStore,
        IDriftNotifier notifier,
        IEscalationService escalationService,
        IKnowledgeGraphStore graphStore,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<DefaultDriftDetectionService> logger)
    {
        _scorer = scorer;
        _baselineStore = baselineStore;
        _auditStore = auditStore;
        _notifier = notifier;
        _escalationService = escalationService;
        _graphStore = graphStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<DriftScore>> EvaluateDriftAsync(DriftEvaluationRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var config = _options.CurrentValue.AI.DriftDetection;
        var now = _timeProvider.GetUtcNow();

        if (!config.Enabled)
        {
            sw.Stop();
            DriftMetrics.EvaluationDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            return Result<DriftScore>.Success(new DriftScore
            {
                ScoreId = Guid.NewGuid(),
                BaselineId = Guid.Empty,
                Scope = request.Scope,
                ScopeIdentifier = request.ScopeIdentifier,
                Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>().AsReadOnly(),
                OverallDrift = 0.0,
                Severity = DriftSeverity.None,
                ScoredAt = now
            });
        }

        var baseline = await ResolveBaselineWithFallbackAsync(request.Scope, request.ScopeIdentifier, ct);
        if (baseline is null)
        {
            sw.Stop();
            DriftMetrics.EvaluationDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            return Result<DriftScore>.Fail($"No baseline available for scope {request.Scope}:{request.ScopeIdentifier}");
        }

        var dimensionScores = new Dictionary<DriftDimension, DriftDimensionScore>();
        foreach (var (dimension, currentValue) in request.Dimensions)
        {
            var scoreResult = await _scorer.ScoreDimensionAsync(dimension, currentValue, baseline, ct);
            if (scoreResult.IsSuccess)
                dimensionScores[dimension] = scoreResult.Value!;
            else
                _logger.LogWarning("Scorer failed for {Dimension}: {Errors}", dimension, string.Join(", ", scoreResult.Errors));
        }

        if (dimensionScores.Count == 0)
        {
            sw.Stop();
            DriftMetrics.EvaluationDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            return Result<DriftScore>.Fail("All dimensions failed scoring");
        }

        var overallDrift = dimensionScores.Values.Max(d => d.Deviation);
        var severity = DriftSeverityClassifier.Classify(overallDrift, config);

        var score = new DriftScore
        {
            ScoreId = Guid.NewGuid(),
            BaselineId = baseline.BaselineId,
            Scope = request.Scope,
            ScopeIdentifier = request.ScopeIdentifier,
            Dimensions = dimensionScores.AsReadOnly(),
            OverallDrift = overallDrift,
            Severity = severity,
            ScoredAt = now
        };

        if (severity >= DriftSeverity.Warn)
        {
            await SafeExecuteAsync("graph persistence",
                () => PersistDriftEventAsync(score, now, ct));

            if (severity == DriftSeverity.Escalate && config.EscalationEnabled)
                await SafeExecuteAsync("escalation",
                    () => TriggerEscalationAsync(score, now, ct));

            await SafeExecuteAsync("notification",
                () => _notifier.NotifyDriftDetectedAsync(score, ct));
        }

        await SafeExecuteAsync("audit",
            () => RecordAuditAsync(score, DriftAuditRecordType.Detected, now, ct));

        DriftMetrics.Evaluations.Add(1,
            new(DriftConventions.Scope, score.Scope.ToString().ToLowerInvariant()),
            new(DriftConventions.Severity, severity.ToString().ToLowerInvariant()));

        sw.Stop();
        DriftMetrics.EvaluationDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        return Result<DriftScore>.Success(score);
    }

    /// <inheritdoc />
    public async Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct) =>
        await _baselineStore.GetBaselineAsync(scope, scopeIdentifier, ct);

    /// <inheritdoc />
    public async Task<Result<DriftBaseline>> UpdateBaselineAsync(DriftBaselineUpdateRequest request, CancellationToken ct)
    {
        var config = _options.CurrentValue.AI.DriftDetection;
        var now = _timeProvider.GetUtcNow();

        if (!config.Enabled)
            return Result<DriftBaseline>.Fail("Drift detection is disabled");

        var historyQuery = new DriftHistoryQuery
        {
            Scope = request.Scope,
            ScopeIdentifier = request.ScopeIdentifier,
            Start = now.AddDays(-config.BaselineWindowDays),
            End = now
        };

        var historyResult = await GetDriftHistoryAsync(historyQuery, ct);
        if (!historyResult.IsSuccess)
            return Result<DriftBaseline>.Fail(historyResult.Errors.ToArray());

        var scores = historyResult.Value!;
        if (scores.Count < config.MinSamplesForBaseline)
            return Result<DriftBaseline>.Fail(
                $"Insufficient samples: {scores.Count}/{config.MinSamplesForBaseline}");

        var dimensionMeans = new Dictionary<DriftDimension, double>();
        var dimensionSigmas = new Dictionary<DriftDimension, double>();

        var allDimensions = scores
            .SelectMany(s => s.Dimensions.Keys)
            .Distinct();

        foreach (var dim in allDimensions)
        {
            var values = scores
                .Where(s => s.Dimensions.ContainsKey(dim))
                .Select(s => s.Dimensions[dim].CurrentValue)
                .ToList();

            if (values.Count == 0) continue;

            var mean = values.Average();
            var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            dimensionMeans[dim] = mean;
            dimensionSigmas[dim] = Math.Sqrt(variance);
        }

        var baseline = new DriftBaseline
        {
            BaselineId = Guid.NewGuid(),
            Scope = request.Scope,
            ScopeIdentifier = request.ScopeIdentifier,
            Dimensions = dimensionMeans.AsReadOnly(),
            DimensionSigmas = dimensionSigmas.AsReadOnly(),
            SampleCount = scores.Count,
            WindowStart = historyQuery.Start,
            WindowEnd = historyQuery.End,
            CreatedAt = now
        };

        var saveResult = await _baselineStore.SaveBaselineAsync(baseline, ct);
        if (!saveResult.IsSuccess)
            return Result<DriftBaseline>.Fail(saveResult.Errors.ToArray());

        await SafeExecuteAsync("audit",
            () => RecordAuditAsync(null, DriftAuditRecordType.BaselineUpdated, now, ct));

        DriftMetrics.BaselinesUpdated.Add(1);
        return Result<DriftBaseline>.Success(baseline);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DriftScore>>> GetDriftHistoryAsync(DriftHistoryQuery query, CancellationToken ct)
    {
        try
        {
            var ownerId = $"{query.Scope}:{query.ScopeIdentifier}";
            var ownerNodes = await _graphStore.GetNodesByOwnerAsync(ownerId, ct);
            var driftScores = ownerNodes
                .Where(n => n.Type == "DriftEvent")
                .Where(n =>
                {
                    if (!n.Properties.TryGetValue("ScoredAt", out var ts)) return false;
                    if (!DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, out var scored)) return false;
                    return scored >= query.Start && scored <= query.End;
                })
                .Select(n => DeserializeDriftScore(n))
                .Where(s => s is not null)
                .Cast<DriftScore>()
                .ToList();

            return Result<IReadOnlyList<DriftScore>>.Success(driftScores.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query drift history");
            return Result<IReadOnlyList<DriftScore>>.Fail($"Failed to query drift history: {ex.Message}");
        }
    }

    private async Task<DriftBaseline?> ResolveBaselineWithFallbackAsync(
        DriftScope requestedScope, string scopeIdentifier, CancellationToken ct)
    {
        var startIndex = Array.IndexOf(FallbackOrder, requestedScope);
        if (startIndex < 0) startIndex = 0;

        for (var i = startIndex; i < FallbackOrder.Length; i++)
        {
            var scope = FallbackOrder[i];
            var result = await _baselineStore.GetBaselineAsync(scope, scopeIdentifier, ct);
            if (result.IsSuccess && result.Value is not null)
                return result.Value;
        }

        return null;
    }

    private async Task PersistDriftEventAsync(DriftScore score, DateTimeOffset now, CancellationToken ct)
    {
        var eventId = Guid.NewGuid();
        var node = new GraphNode
        {
            Id = $"driftevent:{eventId}",
            Name = $"DriftEvent:{score.Scope}:{score.ScopeIdentifier}",
            Type = "DriftEvent",
            OwnerId = $"{score.Scope}:{score.ScopeIdentifier}",
            Properties = new Dictionary<string, string>
            {
                ["EventId"] = eventId.ToString(),
                ["ScoreId"] = score.ScoreId.ToString(),
                ["BaselineId"] = score.BaselineId.ToString(),
                ["Scope"] = score.Scope.ToString(),
                ["ScopeIdentifier"] = score.ScopeIdentifier,
                ["Severity"] = score.Severity.ToString(),
                ["OverallDrift"] = score.OverallDrift.ToString(CultureInfo.InvariantCulture),
                ["ScoredAt"] = score.ScoredAt.ToString("o"),
                ["DimensionsJson"] = JsonSerializer.Serialize(score.Dimensions)
            }.AsReadOnly(),
            CreatedAt = now
        };

        await _graphStore.AddNodesAsync([node], ct);
    }

    private async Task TriggerEscalationAsync(DriftScore score, DateTimeOffset now, CancellationToken ct)
    {
        var driftedDimensions = score.Dimensions
            .Where(d => d.Value.Deviation >= _options.CurrentValue.AI.DriftDetection.WarnThresholdSigma)
            .Select(d => $"{d.Key}: {d.Value.Deviation:F2}σ");

        var request = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = score.ScopeIdentifier,
            ToolName = "drift_detection",
            Arguments = new Dictionary<string, string>
            {
                ["score_id"] = score.ScoreId.ToString(),
                ["severity"] = score.Severity.ToString()
            }.AsReadOnly(),
            Description = $"Drift detected in {score.Scope}:{score.ScopeIdentifier} — " +
                          $"overall {score.OverallDrift:F2}σ. Dimensions: {string.Join(", ", driftedDimensions)}",
            RiskLevel = RiskLevel.High,
            Priority = EscalationPriority.Blocking,
            ApprovalStrategy = ApprovalStrategyType.AnyOf,
            Approvers = [],
            RequestedAt = now
        };

        await _escalationService.QueueEscalationAsync(request, ct);
        DriftMetrics.EscalationsTriggered.Add(1);
    }

    private async Task RecordAuditAsync(DriftScore? score, DriftAuditRecordType recordType, DateTimeOffset now, CancellationToken ct)
    {
        var record = new DriftAuditRecord
        {
            RecordId = Guid.NewGuid(),
            EventId = score?.ScoreId ?? Guid.Empty,
            RecordType = recordType,
            Payload = score is not null ? JsonSerializer.Serialize(score) : "{}",
            RecordedAt = now
        };

        await _auditStore.RecordAsync(record, ct);
    }

    private async Task SafeExecuteAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Non-critical operation '{Operation}' failed during drift evaluation", operation);
        }
    }

    private DriftScore? DeserializeDriftScore(GraphNode node)
    {
        try
        {
            var dimensions = node.Properties.TryGetValue("DimensionsJson", out var json)
                ? JsonSerializer.Deserialize<Dictionary<DriftDimension, DriftDimensionScore>>(json) ?? []
                : [];

            return new DriftScore
            {
                ScoreId = Guid.Parse(node.Properties.GetValueOrDefault("ScoreId", Guid.Empty.ToString())),
                BaselineId = Guid.TryParse(node.Properties.GetValueOrDefault("BaselineId", ""), out var bid) ? bid : Guid.Empty,
                Scope = Enum.Parse<DriftScope>(node.Properties["Scope"]),
                ScopeIdentifier = node.Properties["ScopeIdentifier"],
                Dimensions = dimensions.AsReadOnly(),
                OverallDrift = double.Parse(node.Properties.GetValueOrDefault("OverallDrift", "0"), CultureInfo.InvariantCulture),
                Severity = Enum.Parse<DriftSeverity>(node.Properties["Severity"]),
                ScoredAt = DateTimeOffset.Parse(node.Properties["ScoredAt"], CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize drift score from graph node {NodeId}", node.Id);
            return null;
        }
    }
}

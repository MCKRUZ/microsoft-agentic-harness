using System.Text.Json;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.DriftDetection;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Learnings;

/// <summary>
/// Adjusts drift baselines when high-confidence learnings originating from drift events
/// receive sufficient positive feedback. Resolves the originating drift event and records
/// an audit trail.
/// </summary>
/// <remarks>
/// <para>Invoked by <c>ImproveLearningCommandHandler</c> after each feedback weight update.
/// The bridge is a best-effort side effect — failures are propagated but do not roll back
/// the learning update itself.</para>
/// <para>Idempotent: if the drift event is already resolved, subsequent calls no-op.</para>
/// </remarks>
public sealed class LearningsDriftBridge : ILearningsDriftBridge
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly IDriftDetectionService _driftService;
    private readonly IDriftAuditStore _auditStore;
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LearningsDriftBridge> _logger;

    /// <summary>Initializes a new instance of the <see cref="LearningsDriftBridge"/> class.</summary>
    public LearningsDriftBridge(
        IDriftDetectionService driftService,
        IDriftAuditStore auditStore,
        IKnowledgeGraphStore graphStore,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<LearningsDriftBridge> logger)
    {
        _driftService = driftService;
        _auditStore = auditStore;
        _graphStore = graphStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> CheckAndAdjustBaselineAsync(LearningEntry learning, CancellationToken ct)
    {
        var config = _options.CurrentValue.AI;

        if (!config.Learnings.Enabled || !config.DriftDetection.Enabled)
            return Result.Success();

        if (learning.Source.SourceType != LearningSourceType.DriftDetection)
            return Result.Success();

        if (learning.FeedbackWeight < config.Learnings.BaselineAdjustmentThreshold)
            return Result.Success();

        var nodeId = $"driftevent:{learning.Source.SourceId}";
        var node = await _graphStore.GetNodeAsync(nodeId, ct);
        if (node is null)
        {
            _logger.LogWarning(
                "Drift event node {NodeId} not found — may have been pruned. Skipping baseline adjustment.",
                nodeId);
            return Result.Success();
        }

        if (node.Properties.ContainsKey("Resolution"))
        {
            _logger.LogDebug(
                "Drift event {NodeId} already resolved. Skipping duplicate baseline adjustment.",
                nodeId);
            return Result.Success();
        }

        if (!node.Properties.TryGetValue("Scope", out var scopeStr) ||
            !Enum.TryParse<DriftScope>(scopeStr, out var scope) ||
            !node.Properties.TryGetValue("ScopeIdentifier", out var scopeIdentifier))
        {
            _logger.LogWarning(
                "Drift event node {NodeId} has corrupted scope data. Skipping baseline adjustment.",
                nodeId);
            return Result.Success();
        }

        var updateRequest = new DriftBaselineUpdateRequest
        {
            Scope = scope,
            ScopeIdentifier = scopeIdentifier
        };

        var updateResult = await _driftService.UpdateBaselineAsync(updateRequest, ct);
        if (!updateResult.IsSuccess)
            return Result.Fail(updateResult.Errors.ToArray());

        var now = _timeProvider.GetUtcNow();

        try
        {
            await RecordAuditAsync(node, learning, now, ct);
            await ResolveEventNodeAsync(node, learning, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Post-baseline side effects failed for learning {LearningId}. Baseline was updated successfully.",
                learning.LearningId);
        }

        _logger.LogInformation(
            "Baseline adjusted for {Scope}:{ScopeIdentifier} from learning {LearningId} (weight: {Weight:F3}).",
            scope, scopeIdentifier, learning.LearningId, learning.FeedbackWeight);

        return Result.Success();
    }

    private async Task RecordAuditAsync(
        GraphNode node, LearningEntry learning, DateTimeOffset now, CancellationToken ct)
    {
        var eventId = Guid.TryParse(node.Properties.GetValueOrDefault("EventId", ""), out var eid)
            ? eid
            : Guid.Empty;

        var auditRecord = new DriftAuditRecord
        {
            RecordId = Guid.NewGuid(),
            EventId = eventId,
            RecordType = DriftAuditRecordType.BaselineUpdated,
            Payload = JsonSerializer.Serialize(new
            {
                LearningId = learning.LearningId,
                FeedbackWeight = learning.FeedbackWeight,
                Threshold = _options.CurrentValue.AI.Learnings.BaselineAdjustmentThreshold
            }, s_jsonOptions),
            RecordedAt = now
        };

        var result = await _auditStore.RecordAsync(auditRecord, ct);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed to record baseline-adjusted audit for event {EventId}.", eventId);
        }
    }

    private async Task ResolveEventNodeAsync(
        GraphNode node, LearningEntry learning, DateTimeOffset now, CancellationToken ct)
    {
        var resolution = new DriftResolution
        {
            ResolvedBy = DriftResolutionType.BaselineAdjusted,
            ResolutionId = learning.LearningId.ToString(),
            ResolvedAt = now
        };

        var updatedProperties = new Dictionary<string, string>(node.Properties)
        {
            ["Resolution"] = JsonSerializer.Serialize(resolution, s_jsonOptions)
        };

        var updatedNode = node with { Properties = updatedProperties.AsReadOnly() };
        await _graphStore.AddNodesAsync([updatedNode], ct);
    }
}

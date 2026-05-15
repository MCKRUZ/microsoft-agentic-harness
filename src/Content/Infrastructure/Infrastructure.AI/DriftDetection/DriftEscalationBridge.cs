using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Escalation;
using Application.Core.CQRS.Learnings;
using Domain.AI.DriftDetection;
using Domain.AI.Escalation;
using Domain.AI.Learnings;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Bridges escalation resolutions back into the drift detection and learnings systems.
/// When a drift-originated escalation resolves, updates the drift event and optionally
/// creates a learning entry from approver corrections.
/// </summary>
/// <remarks>
/// Registered as an <see cref="IEscalationNotificationChannel"/> in DI. The
/// <c>CompositeEscalationNotifier</c> automatically fans out to this channel.
/// Only processes escalations where <c>ToolName == "drift_detection"</c>.
/// </remarks>
public sealed class DriftEscalationBridge : IEscalationNotificationChannel
{
    /// <summary>Convention: tool name used by drift detection when queuing escalations.</summary>
    public const string DriftDetectionToolName = "drift_detection";

    /// <summary>
    /// Confidence for escalation-resolution learnings. High because a human reviewed,
    /// but not 1.0 because approver corrections may be contextual.
    /// </summary>
    private const double EscalationResolutionConfidence = 0.8;

    private readonly ConcurrentDictionary<Guid, EscalationRequest> _trackedDriftEscalations = new();
    private readonly IDriftNotifier _driftNotifier;
    private readonly ISender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriftEscalationBridge> _logger;

    /// <summary>Initializes a new <see cref="DriftEscalationBridge"/>.</summary>
    public DriftEscalationBridge(
        IDriftNotifier driftNotifier,
        ISender sender,
        TimeProvider timeProvider,
        ILogger<DriftEscalationBridge> logger)
    {
        _driftNotifier = driftNotifier;
        _sender = sender;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
    {
        if (string.Equals(request.ToolName, DriftDetectionToolName, StringComparison.Ordinal))
        {
            _trackedDriftEscalations.TryAdd(request.EscalationId, request);
            _logger.LogDebug("Tracking drift-originated escalation {EscalationId}.", request.EscalationId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
    {
        if (!_trackedDriftEscalations.TryRemove(outcome.EscalationId, out var request))
            return;

        try
        {
            await NotifyDriftResolvedAsync(request, outcome, ct);
            await TryCreateLearningAsync(request, outcome, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to process drift escalation resolution for {EscalationId}.",
                outcome.EscalationId);
        }
    }

    /// <inheritdoc />
    public Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
    {
        if (_trackedDriftEscalations.TryRemove(request.EscalationId, out _))
        {
            _logger.LogDebug("Removed expiring drift escalation {EscalationId} from tracking.", request.EscalationId);
        }

        return Task.CompletedTask;
    }

    private async Task NotifyDriftResolvedAsync(
        EscalationRequest request, EscalationOutcome outcome, CancellationToken ct)
    {
        var resolution = new DriftResolution
        {
            ResolvedBy = DriftResolutionType.EscalationResolved,
            ResolutionId = outcome.EscalationId.ToString(),
            ResolvedAt = outcome.ResolvedAt,
        };

        var driftEvent = new DriftEvent
        {
            EventId = outcome.EscalationId,
            DriftScore = BuildMinimalDriftScore(request),
            Resolution = resolution,
            DetectedAt = request.RequestedAt,
        };

        await _driftNotifier.NotifyDriftResolvedAsync(driftEvent, ct);

        _logger.LogInformation("Drift escalation {EscalationId} resolved via {ResolutionType}.",
            outcome.EscalationId, outcome.ResolutionType);
    }

    private async Task TryCreateLearningAsync(
        EscalationRequest request, EscalationOutcome outcome, CancellationToken ct)
    {
        var reasons = outcome.Decisions
            .Where(d => !string.IsNullOrWhiteSpace(d.Reason))
            .Select(d => $"Correction from {d.ApproverName}: {d.Reason}")
            .ToList();

        if (reasons.Count == 0)
            return;

        var content = string.Join("; ", reasons);
        var category = outcome.IsApproved
            ? LearningCategory.InstructionUpdate
            : LearningCategory.FactualCorrection;

        var command = new RememberCommand
        {
            Content = content,
            Category = category,
            Scope = new LearningScope { AgentId = request.AgentId },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.EscalationResolution,
                SourceId = outcome.EscalationId.ToString(),
                SourceDescription = $"Escalation resolution for drift in {request.AgentId}",
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "DriftEscalationBridge",
                OriginTask = "escalation_resolution",
                OriginTimestamp = _timeProvider.GetUtcNow(),
                Confidence = EscalationResolutionConfidence,
            },
        };

        await _sender.Send(command, ct);

        _logger.LogInformation("Created learning from drift escalation {EscalationId} with {ReasonCount} corrections.",
            outcome.EscalationId, reasons.Count);
    }

    private static DriftScore BuildMinimalDriftScore(EscalationRequest request) => new()
    {
        ScoreId = Guid.TryParse(
            request.Arguments.GetValueOrDefault("score_id"), out var scoreId) ? scoreId : Guid.Empty,
        BaselineId = Guid.Empty,
        Scope = DriftScope.Agent,
        ScopeIdentifier = request.AgentId,
        Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>().AsReadOnly(),
        OverallDrift = 0,
        Severity = Enum.TryParse<DriftSeverity>(
            request.Arguments.GetValueOrDefault("severity"), out var sev) ? sev : DriftSeverity.Escalate,
        ScoredAt = request.RequestedAt,
    };
}

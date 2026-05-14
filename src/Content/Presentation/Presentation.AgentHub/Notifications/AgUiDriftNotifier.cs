using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.AgUi;

namespace Presentation.AgentHub.Notifications;

/// <summary>
/// AG-UI notification channel for drift detection events. Translates domain drift
/// records into AG-UI SSE events and writes them to the active run's event stream.
/// </summary>
/// <remarks>
/// If no AG-UI run is active (i.e., drift was detected from the ConsoleUI
/// or a non-SSE context), the notifier silently skips event emission. This is by design --
/// drift notifications also flow through other channels (logging, audit store).
/// </remarks>
public sealed class AgUiDriftNotifier : IDriftNotificationChannel
{
    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly ILogger<AgUiDriftNotifier> _logger;

    /// <summary>Initializes a new <see cref="AgUiDriftNotifier"/>.</summary>
    public AgUiDriftNotifier(
        IAgUiEventWriterAccessor writerAccessor,
        ILogger<AgUiDriftNotifier> logger)
    {
        _writerAccessor = writerAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping drift-detected event for {Scope}:{ScopeIdentifier}.",
                score.Scope, score.ScopeIdentifier);
            return;
        }

        var evt = MapToEvent(score);
        if (evt is null)
            return;

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write drift-detected event for {Scope}:{ScopeIdentifier}.",
                score.Scope, score.ScopeIdentifier);
        }
    }

    /// <inheritdoc />
    public async Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping drift-resolved event for {EventId}.",
                driftEvent.EventId);
            return;
        }

        if (driftEvent.Resolution is null)
        {
            _logger.LogWarning("Cannot emit resolved event for unresolved drift {EventId}.", driftEvent.EventId);
            return;
        }

        var evt = new DriftResolvedEvent
        {
            EventId = driftEvent.EventId.ToString(),
            ResolutionType = driftEvent.Resolution.ResolvedBy.ToString(),
            ResolvedBy = driftEvent.Resolution.ResolutionId,
            ResolvedAt = driftEvent.Resolution.ResolvedAt,
        };

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write drift-resolved event for {EventId}.",
                driftEvent.EventId);
        }
    }

    private static AgUiEvent? MapToEvent(DriftScore score)
    {
        if (score.Severity == DriftSeverity.None)
            return null;

        var dimensions = score.Dimensions.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => kvp.Value.Deviation);

        return score.Severity switch
        {
            DriftSeverity.Warn => new DriftWarnEvent
            {
                Scope = score.Scope.ToString(),
                ScopeIdentifier = score.ScopeIdentifier,
                Dimensions = dimensions,
                MaxDeviation = score.OverallDrift,
                Severity = score.Severity.ToString(),
            },
            DriftSeverity.Alert => new DriftAlertEvent
            {
                Scope = score.Scope.ToString(),
                ScopeIdentifier = score.ScopeIdentifier,
                Dimensions = dimensions,
                MaxDeviation = score.OverallDrift,
                Severity = score.Severity.ToString(),
                BaselineId = score.BaselineId.ToString(),
            },
            DriftSeverity.Escalate => new DriftEscalateEvent
            {
                Scope = score.Scope.ToString(),
                ScopeIdentifier = score.ScopeIdentifier,
                Dimensions = dimensions,
                MaxDeviation = score.OverallDrift,
                Severity = score.Severity.ToString(),
                BaselineId = score.BaselineId.ToString(),
                EscalationId = string.Empty,
            },
            _ => null,
        };
    }
}

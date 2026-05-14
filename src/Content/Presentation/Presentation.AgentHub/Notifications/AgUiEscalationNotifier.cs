using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.AgUi;

namespace Presentation.AgentHub.Notifications;

/// <summary>
/// AG-UI notification channel for escalation events. Translates domain escalation
/// records into AG-UI SSE events and writes them to the active run's event stream.
/// </summary>
/// <remarks>
/// If no AG-UI run is active (i.e., the escalation was triggered from the ConsoleUI
/// or a non-SSE context), the notifier silently skips event emission. This is by design --
/// escalation notifications also flow through other channels (Slack, Teams).
/// </remarks>
public sealed class AgUiEscalationNotifier : IEscalationNotificationChannel
{
    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly ILogger<AgUiEscalationNotifier> _logger;

    /// <summary>
    /// Initializes a new <see cref="AgUiEscalationNotifier"/>.
    /// </summary>
    public AgUiEscalationNotifier(
        IAgUiEventWriterAccessor writerAccessor,
        ILogger<AgUiEscalationNotifier> logger)
    {
        _writerAccessor = writerAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping escalation-requested event for {EscalationId}.",
                request.EscalationId);
            return;
        }

        var evt = new EscalationRequestedEvent
        {
            EscalationId = request.EscalationId.ToString(),
            AgentId = request.AgentId,
            ToolName = request.ToolName,
            Description = request.Description,
            Priority = request.Priority.ToString(),
            Approvers = request.Approvers,
            TimeoutSeconds = request.TimeoutSeconds,
            Arguments = request.Arguments.Count > 0
                ? request.Arguments
                : null,
        };

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write escalation-requested event for {EscalationId}.",
                request.EscalationId);
        }
    }

    /// <inheritdoc />
    public async Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping escalation-resolved event for {EscalationId}.",
                outcome.EscalationId);
            return;
        }

        var decisions = outcome.Decisions.Count > 0
            ? outcome.Decisions.Select(d => new AgUiApproverDecision
            {
                ApproverName = d.ApproverName,
                Approved = d.Approved,
                Reason = d.Reason,
            }).ToList()
            : null;

        var evt = new EscalationResolvedEvent
        {
            EscalationId = outcome.EscalationId.ToString(),
            IsApproved = outcome.IsApproved,
            ResolutionType = outcome.ResolutionType.ToString(),
            ResolvedAt = outcome.ResolvedAt,
            Decisions = decisions,
        };

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write escalation-resolved event for {EscalationId}.",
                outcome.EscalationId);
        }
    }

    /// <inheritdoc />
    public async Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
    {
        var writer = _writerAccessor.Writer;
        if (writer is null)
        {
            _logger.LogDebug("No AG-UI writer active; skipping escalation-expiring event for {EscalationId}.",
                request.EscalationId);
            return;
        }

        var evt = new EscalationExpiringEvent
        {
            EscalationId = request.EscalationId.ToString(),
            RemainingSeconds = Math.Max(0, (int)remaining.TotalSeconds),
        };

        try
        {
            await writer.WriteAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write escalation-expiring event for {EscalationId}.",
                request.EscalationId);
        }
    }
}

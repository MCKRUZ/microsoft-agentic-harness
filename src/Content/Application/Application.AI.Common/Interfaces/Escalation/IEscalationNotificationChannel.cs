using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// A single delivery channel for escalation notifications (e.g., AG-UI, Slack, Teams).
/// </summary>
/// <remarks>
/// Implement this interface to add a new notification channel. Register the implementation
/// as <c>IEscalationNotificationChannel</c> in DI -- the <c>CompositeEscalationNotifier</c>
/// automatically discovers and fans out to all registered channels.
/// <para>
/// Implementations MUST be idempotent and MUST NOT throw exceptions that would block
/// other channels. The composite catches and logs per-channel failures.
/// </para>
/// </remarks>
public interface IEscalationNotificationChannel
{
    /// <summary>Notifies approvers that a new escalation requires their attention.</summary>
    Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>Notifies interested parties that an escalation has been resolved.</summary>
    Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct);

    /// <summary>Warns approvers that an escalation is about to expire.</summary>
    Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct);
}

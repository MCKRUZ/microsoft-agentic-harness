using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Delivers escalation notifications to human reviewers.
/// </summary>
/// <remarks>
/// The default implementation (<c>CompositeEscalationNotifier</c>) fans out to all
/// registered <see cref="IEscalationNotificationChannel"/> instances. Individual channel
/// failures are caught and logged without blocking other channels.
/// <para>
/// This interface is the public contract consumed by <c>IEscalationService</c>.
/// To add a new delivery channel, implement <see cref="IEscalationNotificationChannel"/>
/// and register it in DI -- do NOT implement this interface directly.
/// </para>
/// </remarks>
public interface IEscalationNotifier
{
    /// <summary>Notifies approvers that a new escalation requires their attention.</summary>
    Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>Notifies interested parties that an escalation has been resolved.</summary>
    Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct);

    /// <summary>Warns approvers that an escalation is about to expire.</summary>
    Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct);
}

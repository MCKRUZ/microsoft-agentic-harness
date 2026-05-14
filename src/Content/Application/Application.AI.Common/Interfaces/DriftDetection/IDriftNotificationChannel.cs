using Domain.AI.DriftDetection;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Individual notification channel for drift events (AG-UI SSE, logging, etc.).
/// Multiple channels are registered and dispatched by <see cref="IDriftNotifier"/>.
/// </summary>
public interface IDriftNotificationChannel
{
    /// <summary>Notifies that drift has been detected above threshold.</summary>
    Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct);

    /// <summary>Notifies that a previously detected drift has been resolved.</summary>
    Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct);
}

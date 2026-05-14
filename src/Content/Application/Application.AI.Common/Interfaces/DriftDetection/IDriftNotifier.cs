using Domain.AI.DriftDetection;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Composite dispatcher that fans out drift notifications to all registered
/// <see cref="IDriftNotificationChannel"/> instances. Consumed by <see cref="IDriftDetectionService"/>.
/// </summary>
public interface IDriftNotifier
{
    /// <summary>Dispatches drift-detected notification to all channels.</summary>
    Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct);

    /// <summary>Dispatches drift-resolved notification to all channels.</summary>
    Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct);
}

using Domain.AI.Learnings;

namespace Application.AI.Common.Interfaces.Learnings;

/// <summary>
/// Notification channel for learning lifecycle events.
/// Implementations include AG-UI SSE, logging, and audit sinks.
/// </summary>
public interface ILearningNotificationChannel
{
    /// <summary>Notifies that a new learning has been captured.</summary>
    Task NotifyLearningCapturedAsync(LearningEntry learning, CancellationToken ct);

    /// <summary>Notifies that a learning was applied during agent execution.</summary>
    Task NotifyLearningAppliedAsync(LearningEntry learning, string agentId, CancellationToken ct);
}

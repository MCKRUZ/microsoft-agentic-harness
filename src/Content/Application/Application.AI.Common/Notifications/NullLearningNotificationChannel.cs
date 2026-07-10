using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;

namespace Application.AI.Common.Notifications;

/// <summary>
/// Default <see cref="ILearningNotificationChannel"/> for hosts without a real-time
/// client transport. All learning lifecycle notifications are dropped silently.
/// </summary>
/// <remarks>
/// Registered as the always-on default in the standard composition root so
/// <c>RememberCommandHandler</c> can resolve its dependency unconditionally. The AgentHub
/// host overrides with a SignalR/AG-UI-backed implementation via last-registration-wins.
/// </remarks>
public sealed class NullLearningNotificationChannel : ILearningNotificationChannel
{
    /// <inheritdoc />
    public Task NotifyLearningCapturedAsync(LearningEntry learning, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyLearningAppliedAsync(LearningEntry learning, string agentId, CancellationToken ct) =>
        Task.CompletedTask;
}

using Application.AI.Common.Interfaces.Orchestration.Magentic;

namespace Application.AI.Common.Services.Orchestration;

/// <summary>
/// Default <see cref="IMagenticProgressNotifier"/> for hosts that show no live workflow progress.
/// Reports nothing and allocates nothing.
/// </summary>
/// <remarks>
/// Benign rather than throwing: a host with nothing watching is the ordinary case, not a misconfiguration,
/// and a workflow must not fail because no one is listening.
/// </remarks>
public sealed class NoOpMagenticProgressNotifier : IMagenticProgressNotifier
{
    /// <summary>The shared instance. Stateless, so one serves every caller.</summary>
    public static readonly NoOpMagenticProgressNotifier Instance = new();

    /// <inheritdoc />
    public Task NotifyWorkflowStartedAsync(
        Guid workflowId, string workflowName, IReadOnlyList<string> participants, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyPlanAsync(Guid workflowId, int planVersion, string planText, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyRoundAsync(Guid workflowId, MagenticRoundReport round, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyPlanReviewRequestedAsync(
        Guid workflowId, string planText, bool planTruncated, bool isStalled, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyWorkflowCompletedAsync(
        Guid workflowId, string completionReason, int roundsExecuted, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyWorkflowFailedAsync(Guid workflowId, string errorCode, CancellationToken ct)
        => Task.CompletedTask;
}

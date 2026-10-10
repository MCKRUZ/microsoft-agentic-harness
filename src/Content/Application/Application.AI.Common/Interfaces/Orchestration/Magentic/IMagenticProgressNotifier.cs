namespace Application.AI.Common.Interfaces.Orchestration.Magentic;

/// <summary>
/// Reports a Magentic workflow's progress — the manager's plan, each coordination round, a plan-review
/// request, and how the run ended — to whatever surface is watching. Implemented by the AG-UI event
/// bridge in the Presentation layer.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="Planner.IPlanProgressNotifier"/>: fire-and-forget, and it must never take the
/// workflow down. An implementation swallows (and logs) its own transport failures rather than throwing;
/// the producer additionally guards every call, so a notifier that does throw cannot fail a run.
/// </para>
/// <para>
/// <strong>Text arrives already treated.</strong> Plan text and instructions are authored by a model, so
/// the producer (<c>MagenticEventSubscriber</c>) sanitizes, redacts and bounds them before they reach
/// this interface. An implementation should still treat them as untrusted when it frames them for its
/// own transport, but it is not the place secrets are removed — a second notifier would otherwise have
/// to remember to.
/// </para>
/// <para>
/// A host that shows no live progress (the console, a batch run) gets a no-op; nothing is required of it.
/// </para>
/// </remarks>
public interface IMagenticProgressNotifier
{
    /// <summary>Notifies that a workflow has started.</summary>
    /// <param name="workflowId">Identifier of the workflow run.</param>
    /// <param name="workflowName">Human-readable workflow name.</param>
    /// <param name="participants">Ids of the participant agents the manager may dispatch to.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyWorkflowStartedAsync(
        Guid workflowId, string workflowName, IReadOnlyList<string> participants, CancellationToken ct);

    /// <summary>Notifies that the manager produced a plan (version 1) or revised it (later versions).</summary>
    /// <param name="workflowId">Identifier of the workflow run.</param>
    /// <param name="planVersion">1 for the initial plan; incremented on each replan.</param>
    /// <param name="planText">The manager's plan, already sanitized, redacted and bounded.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyPlanAsync(Guid workflowId, int planVersion, string planText, CancellationToken ct);

    /// <summary>Notifies that the manager completed a coordination round and chose who acts next.</summary>
    /// <param name="workflowId">Identifier of the workflow run.</param>
    /// <param name="round">The round: who acts next, what they were asked, and the manager's judgements.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyRoundAsync(Guid workflowId, MagenticRoundReport round, CancellationToken ct);

    /// <summary>Notifies that the workflow is paused waiting for a human to review the plan.</summary>
    /// <param name="workflowId">Identifier of the workflow run.</param>
    /// <param name="planText">The plan under review, already sanitized, redacted and bounded.</param>
    /// <param name="planTruncated">
    /// True when the bound cut the plan. A reviewer can only approve what they were shown, so a surface
    /// that shows <paramref name="planText"/> must say when it is not the whole plan.
    /// </param>
    /// <param name="isStalled">Whether the review was triggered by a stall rather than the initial plan.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyPlanReviewRequestedAsync(
        Guid workflowId, string planText, bool planTruncated, bool isStalled, CancellationToken ct);

    /// <summary>Notifies that the workflow ended with a result (including a round or reset limit).</summary>
    /// <param name="workflowId">Identifier of the workflow run.</param>
    /// <param name="completionReason">Why it ended (a <c>MagenticConventions</c> completion reason).</param>
    /// <param name="roundsExecuted">Coordination rounds the run used.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyWorkflowCompletedAsync(Guid workflowId, string completionReason, int roundsExecuted, CancellationToken ct);

    /// <summary>Notifies that the workflow ended without a usable result.</summary>
    /// <param name="workflowId">Identifier of the workflow run.</param>
    /// <param name="errorCode">A stable <c>magentic.*</c> code; never exception text.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyWorkflowFailedAsync(Guid workflowId, string errorCode, CancellationToken ct);
}

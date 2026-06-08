namespace Application.AI.Common.Interfaces.Orchestration.Magentic;

/// <summary>
/// Input descriptor for a Magentic plan-review HITL pause. Captures the workflow
/// identifier, the plan text, whether the pause was triggered by a stall, and
/// (when stall-triggered) the current progress-ledger summary.
/// </summary>
/// <remarks>
/// Built by the Infrastructure-layer event subscriber when it sees a
/// <c>MagenticPlanReviewRequest</c>. Routed through <see cref="IMagenticPlanReviewBridge"/>
/// into the existing escalation surface so plan-review reuses the same approval
/// pipeline as governance escalations.
/// </remarks>
public sealed record MagenticPlanReviewInput
{
    /// <summary>The workflow identifier this review belongs to.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>The symbolic workflow name (for audit display).</summary>
    public required string WorkflowName { get; init; }

    /// <summary>
    /// The plan text the manager is asking the human to sign off on. Captured
    /// from <c>MagenticPlanReviewRequest.Plan</c> as <see cref="string"/> for
    /// transport-agnostic display.
    /// </summary>
    public required string PlanText { get; init; }

    /// <summary>
    /// <see langword="true"/> when the orchestrator entered plan-review because
    /// the workflow stalled (mirrors <c>MagenticPlanReviewRequest.IsStalled</c>).
    /// </summary>
    public required bool IsStalled { get; init; }

    /// <summary>
    /// Compact progress-ledger summary; non-null on stall-triggered reviews,
    /// null on the initial sign-off pause.
    /// </summary>
    public string? ProgressLedgerSummary { get; init; }

    /// <summary>Optional approver identifier propagated from the workflow request.</summary>
    public string? Approver { get; init; }

    /// <summary>Optional timeout (seconds) for the underlying escalation request.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// Outcome of a Magentic plan-review pause: either approved (no revisions) or
/// revised (revision text to be applied by the manager on resumption).
/// </summary>
public sealed record MagenticPlanReviewOutcome
{
    /// <summary>
    /// <see langword="true"/> when the human approved the plan unmodified
    /// (mapped to <c>MagenticPlanReviewResponse.Approve()</c>).
    /// </summary>
    public required bool Approved { get; init; }

    /// <summary>
    /// Revision text to apply, when <see cref="Approved"/> is <see langword="false"/>.
    /// Mapped to <c>MagenticPlanReviewResponse.Revise(string)</c>.
    /// </summary>
    public string? RevisionFeedback { get; init; }
}

/// <summary>
/// Bridge from a Magentic plan-review pause to the harness's HITL escalation
/// pipeline. The Infrastructure event subscriber holds the only producer
/// reference; the only consumer is the Infrastructure HITL bridge implementation
/// that routes through <c>IEscalationService</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kept as an Application interface so the Infrastructure subscriber can be
/// tested without standing up an escalation service: a fake bridge can return
/// canned outcomes for replay tests. Production wiring composes the bridge with
/// <c>IEscalationService.RequestEscalationAsync</c> (blocking) so the workflow
/// pauses for the full human decision lifetime.
/// </para>
/// </remarks>
public interface IMagenticPlanReviewBridge
{
    /// <summary>
    /// Submit a plan-review pause and await the human decision. Returns the
    /// outcome the orchestrator should hand back to MAF via
    /// <c>StreamingRun.SendResponseAsync</c>.
    /// </summary>
    /// <param name="input">The plan-review pause descriptor.</param>
    /// <param name="ct">A cancellation token tied to the workflow lifetime.</param>
    /// <returns>The plan-review outcome.</returns>
    Task<MagenticPlanReviewOutcome> RequestPlanReviewAsync(MagenticPlanReviewInput input, CancellationToken ct);
}

namespace Application.AI.Common.Interfaces.Orchestration.Magentic;

/// <summary>
/// Terminal result of a Magentic workflow invocation. Carries the workflow
/// identifier, the derived counters, the completion reason, and the manager's
/// final output text.
/// </summary>
/// <remarks>
/// All counter fields are <em>harness-derived</em> from the public event stream
/// (one <c>MagenticProgressLedgerUpdatedEvent</c> = one round; one
/// <c>MagenticReplannedEvent</c> = one reset). The MAF internal
/// <c>MagenticTaskContext.TaskCounters</c> is never read.
/// </remarks>
public sealed record MagenticWorkflowResult
{
    /// <summary>The workflow identifier (mirrors <c>MagenticWorkflowRequest.WorkflowId</c>).</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>The symbolic workflow name surfaced on the root span.</summary>
    public required string WorkflowName { get; init; }

    /// <summary>Total coordination rounds executed (derived from the event stream).</summary>
    public required int RoundsExecuted { get; init; }

    /// <summary>Total stall-triggered resets executed (derived from the event stream).</summary>
    public required int ResetsExecuted { get; init; }

    /// <summary>Total plan-review HITL pauses observed (initial + replan signoffs).</summary>
    public required int PlanReviewsExecuted { get; init; }

    /// <summary>
    /// Terminal reason: one of
    /// <c>satisfied</c> | <c>round_limit</c> | <c>reset_limit</c> | <c>error</c>.
    /// Mirrors the value written to <c>gen_ai.orchestration.magentic.completion_reason</c>.
    /// </summary>
    public required string CompletionReason { get; init; }

    /// <summary>
    /// The manager's final output text. <see langword="null"/> when the workflow
    /// terminated before producing output.
    /// </summary>
    public string? FinalOutput { get; init; }

    /// <summary>
    /// Set when <see cref="CompletionReason"/> is <c>error</c>. Captures the
    /// terminal error message without leaking provider stack traces.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

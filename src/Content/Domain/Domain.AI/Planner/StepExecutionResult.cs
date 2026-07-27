using Domain.AI.Attestation;

namespace Domain.AI.Planner;

/// <summary>
/// Result of executing a single plan step, returned by step executors.
/// </summary>
public sealed record StepExecutionResult
{
    /// <summary>Execution outcome status.</summary>
    public required StepExecutionStatus Status { get; init; }

    /// <summary>Step output data. Null if the step produced no output or failed.</summary>
    public string? Output { get; init; }

    /// <summary>Error message if execution failed. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Wall-clock duration of the step execution.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// HMAC-signed attestation for tool execution steps.
    /// Null for non-tool steps or when attestation could not be created.
    /// </summary>
    public ToolExecutionAttestation? Attestation { get; init; }

    /// <summary>
    /// For conditional branch steps: identifies which downstream edge to activate.
    /// Null for non-branching steps.
    /// </summary>
    public PlanStepId? ActiveEdgeTarget { get; init; }

    /// <summary>
    /// True when this failure is a governance denial — the capability envelope, permission chain, or
    /// autonomy ceiling refused the operation — rather than a transient execution failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction is load-bearing, not cosmetic. <see cref="RetryPolicy"/> is plan-authored
    /// data, so a plan can declare <see cref="ErrorRecovery.SkipStep"/> on the very step a policy
    /// blocks; applying that recovery would let the plan author choose the disposition of the check
    /// that constrains the plan author, and the run would report Completed with the denial silently
    /// dropped. An <see cref="ErrorRecovery.Escalate"/> policy is worse: it loops approve → re-run →
    /// deny → escalate again, asking a human to approve something the envelope will never permit.
    /// </para>
    /// <para>
    /// The executor therefore routes a result carrying this flag to a terminal failure that neither
    /// the retry budget nor <see cref="RetryPolicy.OnExhausted"/> can soften — the same reasoning
    /// that keeps a rejected escalation out of the retry policy.
    /// </para>
    /// </remarks>
    public bool IsPolicyDenial { get; init; }
}

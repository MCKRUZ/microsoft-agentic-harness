namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Optional plan-level execution settings a caller may request when submitting a workflow.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is a <em>request</em>, checked against the host's configured ceilings in
/// <c>WorkflowSubmissionConfig</c>. A request above the ceiling is <strong>rejected</strong>, not
/// clamped. Clamping is the friendlier-looking behaviour and the worse one: a caller who asked for a
/// thirty-minute budget and silently received sixty seconds learns the difference from a timeout in
/// production, with nothing in the response to explain it.
/// </para>
/// <para>
/// <c>MaxSubPlanDepth</c> is deliberately absent. It is a runtime recursion guard the host owns, not a
/// per-submission choice — a caller that could raise it could nest arbitrarily deep regardless of the
/// admission cap.
/// </para>
/// </remarks>
public sealed record WorkflowExecutionSettings
{
    /// <summary>
    /// Requested wall-clock budget for the whole workflow. Rejected if above the host's ceiling.
    /// When omitted, the host default applies.
    /// </summary>
    public TimeSpan? PlanTimeout { get; init; }

    /// <summary>
    /// Requested maximum number of steps executed concurrently. Rejected if above the host's ceiling.
    /// Bounds how much work one submission can have in flight at once, which is the practical limit on
    /// what a single caller can cost the host. When omitted, the host default applies.
    /// </summary>
    public int? MaxParallelSteps { get; init; }
}

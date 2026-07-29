namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Configuration for a step that invokes another, previously-submitted workflow as a child.
/// </summary>
/// <remarks>
/// <para>
/// <strong>By reference only.</strong> The domain type also accepts an entire nested plan inline, and
/// that is deliberately not on the wire. An inline child makes the request body recursive, which turns
/// the nesting-depth cap into a parser concern — depth must then be bounded <em>during</em>
/// deserialization, before there is an object to inspect. Referencing a stored workflow keeps the depth
/// check a walk over persisted rows, and keeps every child a separately-admitted, separately-owned
/// plan.
/// </para>
/// <para>
/// <strong>The referenced workflow must be one the caller can see.</strong> A child id that resolves to
/// a plan outside the caller's scope is rejected as a validation failure, not reported as "not found
/// but might exist" — the submission surface must not become an oracle for probing which workflow ids
/// are real.
/// </para>
/// <para>
/// A known limitation, disclosed rather than hidden: a workflow with no owner is treated as global and
/// is therefore visible to everyone, so a sub-plan step can currently reference one. Whether "visible
/// to all" should also mean "runnable by all" is an open design question, not an accident — it is
/// tracked as part of the run surface rather than settled here.
/// </para>
/// </remarks>
public sealed record SubPlanStepConfiguration : WorkflowStepConfiguration
{
    /// <summary>
    /// Identifier of the previously-submitted workflow to invoke as the child. Must resolve to a
    /// workflow visible to the submitting caller.
    /// </summary>
    public required Guid ChildWorkflowId { get; init; }

    /// <summary>
    /// Whether the child runs with its own isolated execution context rather than sharing the parent's.
    /// Defaults to true — isolation is the safer default, and a caller who wants context shared should
    /// have to ask for it.
    /// </summary>
    public bool IsolateContext { get; init; } = true;
}

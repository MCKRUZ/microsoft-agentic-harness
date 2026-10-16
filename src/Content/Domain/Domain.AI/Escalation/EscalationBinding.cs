namespace Domain.AI.Escalation;

/// <summary>
/// The plan step an escalation was queued on behalf of: the one place its verdict may release.
/// </summary>
/// <remarks>
/// <para>
/// A step parked in <c>Blocked</c> finds its escalation through an id kept in the step's own stored
/// output, which lives in the plan state store and is a different record from the escalation. Nothing
/// about that id says the escalation is <em>this step's</em>: repoint the stored id at any approved
/// escalation and the step would be released by a decision a human made about something else. The
/// binding closes that. It is written on the request when the step queues it and copied onto every
/// <see cref="EscalationOutcome"/> the service builds, so it travels inside the sealed outcome bytes,
/// and the plan executor releases a step only when the outcome names that step.
/// </para>
/// <para>
/// Plain <see cref="Guid"/> values rather than the planner's strongly-typed ids: this record is
/// persisted inside escalation JSON, and a Guid pair serialises the same way everywhere.
/// </para>
/// </remarks>
/// <param name="PlanId">The plan the step belongs to.</param>
/// <param name="StepId">The step the escalation blocks.</param>
public sealed record EscalationBinding(Guid PlanId, Guid StepId);

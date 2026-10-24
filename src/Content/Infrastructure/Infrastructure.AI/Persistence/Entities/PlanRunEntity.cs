namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// One run of a plan by one person: the ids the run's steps recorded data under, so right-to-erasure can find them
/// (#906).
/// </summary>
/// <remarks>
/// A model step's conversation id is <c>{RunScope}:{stepId}</c> and its run's budget key <c>planrun:{RunScope}</c>, both
/// derived from the plan's steps when erasure asks; a tool step's spilled results sit under
/// <see cref="ToolResultScopeId"/>. One row per distinct (plan, owner, run scope, tool-result scope), refreshed each time
/// the plan runs under them. Deleted with the plan.
/// </remarks>
public sealed class PlanRunEntity
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The plan that ran. Its steps give the step conversation ids.</summary>
    public Guid PlanGraphId { get; set; }

    /// <summary>
    /// The canonicalized identity of the person who ran it, which for a shared sub-plan is not the plan's owner. Never
    /// null: a run with no caller is not recorded.
    /// </summary>
    public required string OwnerId { get; set; }

    /// <summary>The run scope the model steps derived their conversation ids and the run budget key from.</summary>
    public required string RunScope { get; set; }

    /// <summary>The scope the run's tool steps kept spilled results under.</summary>
    public required string ToolResultScopeId { get; set; }

    /// <summary>When the plan last started or ended under this row's scopes.</summary>
    public DateTimeOffset LastRunAt { get; set; }

    /// <summary>
    /// Whether a run under this row's scopes has started and not yet ended. Erasure keeps the plan of a run in progress,
    /// since its steps are still writing; a mark older than a day is a run that crashed, and is treated as ended.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>The plan that ran.</summary>
    public PlanGraphEntity? PlanGraph { get; set; }
}

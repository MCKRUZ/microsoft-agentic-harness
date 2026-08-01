namespace Domain.AI.Runs;

/// <summary>
/// What kind of work a queued run performs. Selects the executor the dispatcher resolves for a job.
/// </summary>
/// <remarks>
/// An enum rather than a free string, matching the keyed-DI convention already used for plan step
/// executors. Adding a kind is then a deliberate edit here plus a registration, and a job whose kind
/// has no registered executor is a startup-visible gap rather than a typo that silently never runs.
/// </remarks>
public enum RunKind
{
    /// <summary>Executes a stored workflow (plan) through the planner.</summary>
    Workflow = 0,

    /// <summary>Executes a submitted evaluation suite through the eval runner.</summary>
    Evaluation = 1
}

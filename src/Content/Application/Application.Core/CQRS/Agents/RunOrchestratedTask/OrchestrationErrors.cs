namespace Application.Core.CQRS.Agents.RunOrchestratedTask;

/// <summary>
/// Stable error codes for a failed orchestrated task's plan, returned in
/// <see cref="OrchestratedTaskResult.Error"/> in place of model- or exception-authored text.
/// </summary>
/// <remarks>
/// A code rather than a message because the detail — what the model actually returned, which agent name
/// it invented — is untrusted model output and can carry anything; it is logged, never returned.
/// </remarks>
public static class OrchestrationErrors
{
    /// <summary>The planning reply could not be read as a plan, even after the one repair attempt.</summary>
    public const string PlanInvalid = "orchestration.plan_invalid";

    /// <summary>
    /// The planning call itself failed (provider error, content-safety block, unsupported schema request),
    /// as opposed to the model returning a plan that cannot be used.
    /// </summary>
    public const string PlanUnavailable = "orchestration.plan_unavailable";

    /// <summary>The plan parsed but contains no subtasks.</summary>
    public const string PlanEmpty = "orchestration.plan_empty";

    /// <summary>The plan assigns a subtask to an agent that is not among the available agents.</summary>
    public const string PlanUnknownAgent = "orchestration.plan_unknown_agent";
}

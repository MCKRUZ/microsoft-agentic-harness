namespace Domain.AI.Planner;

/// <summary>
/// Defines the action taken when a step exhausts all retry attempts.
/// </summary>
public enum ErrorRecovery
{
    /// <summary>Mark this step as failed; downstream steps may still execute if not dependent.</summary>
    FailStep,

    /// <summary>Skip this step and continue execution as if it completed.</summary>
    SkipStep,

    /// <summary>Terminate the entire plan immediately.</summary>
    FailPlan,

    /// <summary>Escalate to a human operator for manual intervention.</summary>
    Escalate
}

namespace Domain.AI.Planner;

/// <summary>
/// Stable, scrubbed error codes persisted in <see cref="StepExecutionState.ErrorMessage"/> when a
/// step fails from an exception. These are the only failure text a caller ever sees for those paths.
/// </summary>
/// <remarks>
/// <para>
/// Step error state is persisted and returned to callers through
/// <see cref="PlanExecutionSummary.StepStates"/>, which a host surface relays over HTTP. Raw
/// exception text on that path is a leak channel: sandbox, EF Core, and HTTP-client messages carry
/// file-system paths, connection strings, and SAS tokens. Every exception is therefore logged in
/// full via structured logging and reduced to one of these codes before it is persisted.
/// </para>
/// <para>
/// Codes are contract: a consumer may branch on them. Add new ones rather than re-wording existing
/// ones. Failure text that never originates from an exception — a human gate's reason, a governance
/// denial (<see cref="Governance.GovernanceDenials.NotPermitted"/>), a validation message the engine
/// itself composed — is already caller-safe and is not routed through here.
/// </para>
/// </remarks>
public static class PlanStepErrors
{
    /// <summary>An unhandled exception escaped the step executor.</summary>
    public const string ExecutionFailed = "step.execution_failed";

    /// <summary>The sandbox threw while executing a tool step.</summary>
    public const string SandboxFailed = "step.sandbox_failed";

    /// <summary>
    /// The sandbox ran the tool and it reported failure. Distinct from
    /// <see cref="SandboxFailed"/>, which means the sandbox itself could not complete the execution:
    /// this one says the tool ran and did not succeed, so a consumer can tell "your tool errored" from
    /// "we could not run your tool". The sandbox's own failure text is not relayed — it carries raw
    /// process stderr or container logs, where file-system paths, environment variables, and credentials
    /// surface — and stays in the structured log instead.
    /// </summary>
    public const string ToolFailed = "step.tool_failed";

    /// <summary>The step exceeded its per-attempt timeout.</summary>
    public const string Timeout = "step.timeout";

    /// <summary>The retrieval pipeline threw while executing a retrieval step.</summary>
    public const string RetrievalFailed = "step.retrieval_failed";

    /// <summary>The child plan threw while executing a sub-plan step.</summary>
    public const string SubPlanFailed = "step.subplan_failed";

    /// <summary>
    /// The plan run's lifetime conversation token budget was already spent, so the step refused to
    /// start another conversation. A spend control, not an execution fault.
    /// </summary>
    public const string BudgetExhausted = "step.budget_exhausted";

    /// <summary>
    /// The step declared a required autonomy level above the capability envelope's ceiling. Which
    /// tier was required, and what the ceiling was, stay in the structured log: both describe the
    /// caller's grant, which is exactly the operator policy detail this field must not relay.
    /// </summary>
    public const string AutonomyCeilingExceeded = "step.autonomy_ceiling_exceeded";
}

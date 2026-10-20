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
    /// <summary>
    /// A human gate was run without the plan it belongs to, so it had nothing to bind its escalation
    /// to; an unbound escalation could be released by any approval, so the gate refuses to queue one.
    /// </summary>
    public const string HumanGateUnbound = "step.human_gate_unbound";

    /// <summary>
    /// A blocked step's stored escalation reference led to a verdict bound to a different plan or step.
    /// The step is failed rather than released: a verdict nobody made about this step must not decide it.
    /// </summary>
    public const string EscalationBindingMismatch = "step.escalation_binding_mismatch";

    /// <summary>
    /// A blocked step's stored escalation reference led to a verdict bound to no plan step at all: an
    /// escalation queued before bindings existed, or one never queued for a plan step (a tool approval).
    /// Kept apart from <see cref="EscalationBindingMismatch"/> because after an upgrade this is the
    /// expected cause, not a sign of tampering. Retrying the step queues a bound escalation.
    /// </summary>
    public const string EscalationUnbound = "step.escalation_unbound";

    /// <summary>
    /// A blocked step's stored escalation reference led to a verdict for the right step, but not from the
    /// escalation most recently queued for it. The step is failed rather than released: an older approval
    /// must not stand in for the decision the step is actually waiting on.
    /// </summary>
    public const string EscalationSuperseded = "step.escalation_superseded";

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

    /// <summary>
    /// The step names a first-party tool whose published name differs from the key it is registered
    /// under (#888). A grant or deny names a tool by one string, so a tool answering to two cannot be
    /// governed reliably and is refused wherever it is built. Which names disagreed stays in the
    /// structured log; this is a host registration defect, not something the plan author can fix.
    /// </summary>
    public const string ToolMisregistered = "step.tool_misregistered";

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

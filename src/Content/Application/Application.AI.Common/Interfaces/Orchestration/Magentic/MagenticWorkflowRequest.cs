using Microsoft.Agents.AI;

namespace Application.AI.Common.Interfaces.Orchestration.Magentic;

/// <summary>
/// Input descriptor for a Magentic workflow invocation. Captures the manager
/// agent, the set of participant agents, the orchestration limits, and whether
/// the orchestration should pause for a HITL plan-review at start and on each
/// stall-triggered replan.
/// </summary>
/// <remarks>
/// <para>
/// The harness wraps MAF's <c>MagenticWorkflowBuilder</c> via
/// <see cref="IMagenticOrchestrator"/>. Consumers never touch
/// <c>MagenticWorkflowBuilder</c> directly — they build a request and submit it
/// so the orchestrator can wire the OTel event subscriber, the HITL bridge, and
/// the change-proposal router on the same workflow instance.
/// </para>
/// <para>
/// All fields except <see cref="Manager"/>, <see cref="Participants"/>, and
/// <see cref="Task"/> are optional and surface as harness-extension attributes
/// on the root workflow span (<c>gen_ai.orchestration.magentic.*</c>).
/// </para>
/// </remarks>
public sealed record MagenticWorkflowRequest
{
    /// <summary>
    /// The manager agent that synthesizes the plan and drives the coordination
    /// loop. MAF requires a single manager; the harness always names its
    /// in-process invoke_agent span <c>MagenticManager</c> for cross-workflow
    /// pivotability regardless of the underlying <c>AIAgent.Name</c>.
    /// </summary>
    public required AIAgent Manager { get; init; }

    /// <summary>
    /// The pool of participant agents the manager may dispatch to. Order is
    /// preserved into MAF's <c>AddParticipants</c> call; identity is recorded
    /// as a low-cardinality list on the root span for filtering.
    /// </summary>
    public required IReadOnlyList<AIAgent> Participants { get; init; }

    /// <summary>
    /// The user-facing task description seeded as the workflow's initial input.
    /// The manager turns this into its first Task Ledger.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Optional symbolic name for the workflow, surfaced on the root
    /// <c>invoke_workflow magentic.{name}</c> span. When null the harness uses
    /// a synthetic name derived from <see cref="WorkflowId"/>.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Stable identifier for this workflow run. Used to correlate events,
    /// span trees, and HITL escalations. When null the orchestrator generates
    /// a new <see cref="Guid"/> at submission time.
    /// </summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>
    /// Ceiling on coordination rounds. <see langword="null"/> means unbounded.
    /// Mirrors MAF's <c>MagenticWorkflowBuilder.WithMaxRounds</c>.
    /// </summary>
    public int? MaxRounds { get; init; }

    /// <summary>
    /// Ceiling on stalls before the manager forces a replan. MAF default is 3
    /// — kept explicit here so the value surfaces on the workflow span even
    /// when the consumer accepts the default.
    /// </summary>
    public int MaxStalls { get; init; } = 3;

    /// <summary>
    /// Ceiling on stall-triggered resets. <see langword="null"/> means unbounded.
    /// Mirrors MAF's <c>MagenticWorkflowBuilder.WithMaxResets</c>.
    /// </summary>
    public int? MaxResets { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the orchestrator pauses on the initial plan
    /// and on every stall-triggered replan for a HITL plan-review. The harness
    /// HITL bridge routes the pause through the existing
    /// <c>IEscalationService</c>. Mirrors MAF's
    /// <c>MagenticWorkflowBuilder.RequirePlanSignoff</c>.
    /// </summary>
    public bool RequirePlanSignoff { get; init; }

    /// <summary>
    /// Optional approver identifier used to route the HITL plan-review through
    /// <c>IEscalationService</c>. When null the bridge falls back to a
    /// configured default. Only consulted when
    /// <see cref="RequirePlanSignoff"/> is <see langword="true"/>.
    /// </summary>
    public string? PlanReviewApprover { get; init; }

    /// <summary>
    /// Optional timeout (seconds) for each plan-review pause. Surfaces as the
    /// timeout passed to <c>IEscalationService.RequestEscalationAsync</c>. When
    /// null the bridge uses the escalation service's default.
    /// </summary>
    public int? PlanReviewTimeoutSeconds { get; init; }
}

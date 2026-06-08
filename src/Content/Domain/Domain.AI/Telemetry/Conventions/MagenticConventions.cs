namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OpenTelemetry GenAI semantic-convention attribute, span, and event names for
/// Magentic orchestration. The harness emits these from
/// <c>Infrastructure.AI.Orchestration.Magentic</c> while subscribing to the public
/// MAF event stream (<c>MagenticOrchestratorEvent</c> derivatives).
/// </summary>
/// <remarks>
/// <para>
/// The Magentic orchestrator surface in MAF 1.9.0 (April 2026 GA) is still flagged
/// experimental via <c>MAAIW001</c>. The <c>MagenticOrchestrator</c> class itself is
/// <see langword="internal"/>, so instrumentation MUST attach to the public event
/// stream and derive effective counters (rounds, stalls, resets) from observed
/// events — never reach for the internal <c>MagenticTaskContext.TaskCounters</c>.
/// </para>
/// <para>
/// Span tree (authoritative — <c>documentation/architecture/magentic-spans.md</c>):
/// <list type="bullet">
/// <item><description><c>invoke_workflow magentic.{workflow_name}</c> — root, lifetime of the orchestration.</description></item>
/// <item><description><c>invoke_agent MagenticManager</c> — manager span; carries Task Ledger as span events.</description></item>
/// <item><description><c>magentic.round {n}</c> — one per <c>MagenticProgressLedgerUpdatedEvent</c>.</description></item>
/// <item><description><c>magentic.reset {n}</c> — one per <c>MagenticReplannedEvent</c>.</description></item>
/// <item><description><c>magentic.plan_review</c> — lifetime of the HITL pause.</description></item>
/// </list>
/// </para>
/// <para>
/// Spec-covered concepts use the standard <c>gen_ai.*</c> namespace; harness
/// extensions (rounds, plan-review, resets, plan version) use
/// <c>gen_ai.orchestration.magentic.*</c> to stay safely outside the official
/// reserved namespace.
/// </para>
/// </remarks>
public static class MagenticConventions
{
    // ─────────────────────────────────────────────────────────────────────
    // ActivitySource
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Name of the OTel <see cref="System.Diagnostics.ActivitySource"/> used by
    /// the Magentic event subscriber. Single source per process; the harness
    /// observability blueprint registers this with the meter/tracer provider.
    /// </summary>
    public const string ActivitySourceName = "AgenticHarness.Orchestration.Magentic";

    // ─────────────────────────────────────────────────────────────────────
    // Operation values
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>gen_ai.operation.name</c> value on the root Magentic workflow span.
    /// Matches the OTel agent-spans <c>invoke_workflow</c> operation.
    /// </summary>
    public const string OperationInvokeWorkflow = "invoke_workflow";

    /// <summary>
    /// <c>gen_ai.operation.name</c> value on the manager <c>invoke_agent</c> span.
    /// </summary>
    public const string OperationInvokeAgent = "invoke_agent";

    // ─────────────────────────────────────────────────────────────────────
    // Span names
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Span name prefix for the root workflow span. Full name: <c>invoke_workflow magentic.{workflow_name}</c>.</summary>
    public const string SpanNameWorkflowPrefix = "invoke_workflow magentic.";

    /// <summary>Span name for the manager agent span (always <c>invoke_agent MagenticManager</c>).</summary>
    public const string SpanNameManager = "invoke_agent MagenticManager";

    /// <summary>Span name prefix for the per-round coordination span. Full name: <c>magentic.round {round_number}</c>.</summary>
    public const string SpanNameRoundPrefix = "magentic.round ";

    /// <summary>Span name prefix for the stall-triggered reset span. Full name: <c>magentic.reset {reset_number}</c>.</summary>
    public const string SpanNameResetPrefix = "magentic.reset ";

    /// <summary>Span name for the HITL plan-review pause span.</summary>
    public const string SpanNamePlanReview = "magentic.plan_review";

    // ─────────────────────────────────────────────────────────────────────
    // Root workflow span attributes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Workflow name attribute on the root Magentic workflow span. Derived from
    /// the consumer's <see cref="System.String"/> identifier passed into the
    /// workflow request — falls back to a synthetic name when null.
    /// </summary>
    public const string WorkflowName = "gen_ai.workflow.name";

    /// <summary>Configured <c>WithMaxRounds</c> ceiling. Nullable when unbounded.</summary>
    public const string MaxRounds = "gen_ai.orchestration.magentic.max_rounds";

    /// <summary>Configured <c>WithMaxStalls</c> ceiling (MAF default: 3).</summary>
    public const string MaxStalls = "gen_ai.orchestration.magentic.max_stalls";

    /// <summary>Configured <c>WithMaxResets</c> ceiling. Nullable when unbounded.</summary>
    public const string MaxResets = "gen_ai.orchestration.magentic.max_resets";

    /// <summary>Whether <c>RequirePlanSignoff</c> was enabled for this workflow.</summary>
    public const string RequirePlanSignoff = "gen_ai.orchestration.magentic.require_plan_signoff";

    /// <summary>Comma-joined list of participant agent IDs (low-cardinality for filtering).</summary>
    public const string Participants = "gen_ai.orchestration.magentic.participants";

    /// <summary>Derived rounds-executed count, set at root-span end.</summary>
    public const string RoundsExecuted = "gen_ai.orchestration.magentic.rounds_executed";

    /// <summary>Derived resets-executed count, set at root-span end.</summary>
    public const string ResetsExecuted = "gen_ai.orchestration.magentic.resets_executed";

    /// <summary>
    /// Terminal reason for the workflow. Enum string from <see cref="CompletionReasonSatisfied"/>,
    /// <see cref="CompletionReasonRoundLimit"/>, <see cref="CompletionReasonResetLimit"/>,
    /// or <see cref="CompletionReasonError"/>.
    /// </summary>
    public const string CompletionReason = "gen_ai.orchestration.magentic.completion_reason";

    /// <summary>Completion reason value: workflow finished because the request was satisfied.</summary>
    public const string CompletionReasonSatisfied = "satisfied";

    /// <summary>Completion reason value: workflow stopped because the round ceiling was reached.</summary>
    public const string CompletionReasonRoundLimit = "round_limit";

    /// <summary>Completion reason value: workflow stopped because the reset ceiling was reached.</summary>
    public const string CompletionReasonResetLimit = "reset_limit";

    /// <summary>Completion reason value: workflow terminated with an unhandled error.</summary>
    public const string CompletionReasonError = "error";

    // ─────────────────────────────────────────────────────────────────────
    // Manager span attributes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Magentic role discriminator on the manager span (<c>manager</c>) and on
    /// participant invoke_agent spans (<c>participant</c>) — lets filters distinguish
    /// the two without parsing the span name.
    /// </summary>
    public const string Role = "gen_ai.orchestration.magentic.role";

    /// <summary>Role value: orchestration manager (in-process).</summary>
    public const string RoleManager = "manager";

    /// <summary>Role value: dispatched participant agent.</summary>
    public const string RoleParticipant = "participant";

    /// <summary>
    /// Monotonic plan version on the manager span. Initial plan = 1; each span event
    /// mirroring a <c>MagenticReplannedEvent</c> increments by 1. Lets timelines
    /// group rounds under their governing plan.
    /// </summary>
    public const string PlanVersion = "gen_ai.orchestration.magentic.plan.version";

    /// <summary>Span event name mirroring <c>MagenticPlanCreatedEvent</c> on the manager span.</summary>
    public const string EventPlanCreated = "gen_ai.orchestration.magentic.plan_created";

    /// <summary>Span event name mirroring <c>MagenticReplannedEvent</c> on the manager span.</summary>
    public const string EventReplanned = "gen_ai.orchestration.magentic.replanned";

    // ─────────────────────────────────────────────────────────────────────
    // Round span attributes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>1-based round number on the per-round span.</summary>
    public const string RoundNumber = "gen_ai.orchestration.magentic.round.number";

    /// <summary>
    /// Harness-derived stall counter after the round resolves. Increments when
    /// <c>IsInLoop=true</c> or <c>IsProgressBeingMade=false</c>; decrements on a
    /// clean round (floor 0). Mirrors the orchestrator's internal counter.
    /// </summary>
    public const string RoundStallCountAfter = "gen_ai.orchestration.magentic.round.stall_count_after";

    /// <summary>Round progress: <c>MagenticProgressLedger.IsRequestSatisfied</c>.</summary>
    public const string ProgressIsRequestSatisfied = "gen_ai.orchestration.magentic.progress.is_request_satisfied";

    /// <summary>Round progress: <c>MagenticProgressLedger.IsInLoop</c>.</summary>
    public const string ProgressIsInLoop = "gen_ai.orchestration.magentic.progress.is_in_loop";

    /// <summary>Round progress: <c>MagenticProgressLedger.IsProgressBeingMade</c>.</summary>
    public const string ProgressIsProgressBeingMade = "gen_ai.orchestration.magentic.progress.is_progress_being_made";

    /// <summary>Round progress: <c>MagenticProgressLedger.NextSpeaker</c>.</summary>
    public const string ProgressNextSpeaker = "gen_ai.orchestration.magentic.progress.next_speaker";

    /// <summary>Span event mirroring <c>MagenticProgressLedgerUpdatedEvent</c> on the round span.</summary>
    public const string EventProgressLedgerUpdated = "gen_ai.orchestration.magentic.progress_ledger_updated";

    // ─────────────────────────────────────────────────────────────────────
    // Reset span attributes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>1-based reset number on the reset span.</summary>
    public const string ResetNumber = "gen_ai.orchestration.magentic.reset.number";

    /// <summary>
    /// Reset trigger: <see cref="ResetTriggerStall"/> when stall-driven, or
    /// <see cref="ResetTriggerLedgerFailure"/> when the manager's progress-ledger
    /// update threw.
    /// </summary>
    public const string ResetTrigger = "gen_ai.orchestration.magentic.reset.trigger";

    /// <summary>Reset trigger value: stall counter exceeded the configured ceiling.</summary>
    public const string ResetTriggerStall = "stall";

    /// <summary>Reset trigger value: the manager's progress-ledger update threw.</summary>
    public const string ResetTriggerLedgerFailure = "ledger_failure";

    /// <summary>Whether the workflow was stalled at the moment of reset.</summary>
    public const string ResetWasStalled = "gen_ai.orchestration.magentic.reset.was_stalled";

    // ─────────────────────────────────────────────────────────────────────
    // Plan-review span attributes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan-review outcome: <see cref="PlanReviewOutcomeApproved"/> when the
    /// response carries no revision messages, <see cref="PlanReviewOutcomeRevised"/>
    /// otherwise.
    /// </summary>
    public const string PlanReviewOutcome = "gen_ai.orchestration.magentic.plan_review.outcome";

    /// <summary>Plan-review outcome value: approved (zero revision messages).</summary>
    public const string PlanReviewOutcomeApproved = "approved";

    /// <summary>Plan-review outcome value: revised (>=1 revision message).</summary>
    public const string PlanReviewOutcomeRevised = "revised";

    /// <summary>Whether the plan-review pause was triggered by a stall.</summary>
    public const string PlanReviewIsStalled = "gen_ai.orchestration.magentic.plan_review.is_stalled";

    /// <summary>Whether the request carried a current progress ledger (false on initial plan-review).</summary>
    public const string PlanReviewHasProgressLedger = "gen_ai.orchestration.magentic.plan_review.has_progress_ledger";

    /// <summary>Span event marking plan-review submission (start of pause).</summary>
    public const string EventPlanReviewRequested = "gen_ai.orchestration.magentic.plan_review.requested";

    /// <summary>Span event marking plan-review resolution (end of pause).</summary>
    public const string EventPlanReviewResolved = "gen_ai.orchestration.magentic.plan_review.resolved";

    // ─────────────────────────────────────────────────────────────────────
    // Opt-in content-capture attribute keys (PR-11 owns the toggle)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opt-in: full Task-Ledger text on the <see cref="EventPlanCreated"/> and
    /// <see cref="EventReplanned"/> span events. OFF by default per OTel GenAI
    /// content-capture guidance — PR-11 owns the toggle.
    /// </summary>
    public const string PlanContent = "gen_ai.orchestration.magentic.plan.content";

    /// <summary>
    /// Opt-in: <c>MagenticProgressLedger.InstructionOrQuestion</c> on the round span.
    /// OFF by default — PR-11 owns the toggle.
    /// </summary>
    public const string ProgressInstructionOrQuestion = "gen_ai.orchestration.magentic.progress.instruction_or_question";

    /// <summary>
    /// Opt-in: replan reason text on the reset span (e.g., <c>WorkflowWarningEvent</c>
    /// payload when trigger is <see cref="ResetTriggerLedgerFailure"/>). OFF by
    /// default — PR-11 owns the toggle.
    /// </summary>
    public const string ReplanReason = "gen_ai.orchestration.magentic.replan.reason";

    /// <summary>
    /// Opt-in: revision-message text from <c>MagenticPlanReviewResponse</c> on a
    /// <see cref="PlanReviewOutcomeRevised"/> outcome. OFF by default — PR-11
    /// owns the toggle.
    /// </summary>
    public const string PlanReviewFeedback = "gen_ai.orchestration.magentic.plan_review.feedback";
}

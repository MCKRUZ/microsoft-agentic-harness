namespace Domain.Common.Config.AI.WorkflowSubmission;

/// <summary>
/// Root configuration for the workflow-submission surface — the host's ability to accept an
/// externally-authored workflow definition over HTTP, validate it, and persist it as a plan the
/// caller can later run. Bound from <c>AppConfig:AI:WorkflowSubmission</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default — meaning the class default is <see langword="false"/>.</strong> A submitted
/// workflow is untrusted, externally-authored input that ultimately drives model inference, tool
/// invocation and retrieval on the host's credentials, so a consumer who binds this section without
/// setting anything gets no surface at all.
/// </para>
/// <para>
/// <c>Presentation.ExecutionApi</c> sets <c>Enabled: true</c> in its own <c>appsettings.json</c>,
/// because serving this API is that host's entire purpose — exactly as it does for
/// <see cref="BundleExecution.BundleExecutionConfig"/>. The default protects every <em>other</em>
/// host in the solution, and any consumer who copies the section without meaning to enable it.
/// </para>
/// <para>
/// <strong>These caps bound admission, not execution.</strong> They exist to reject a hostile or
/// accidental definition before it is persisted, so the cost of a bad submission is one rejected
/// request rather than a stored plan that misbehaves on every subsequent run.
/// <c>PlanValidator</c> already enforces the *structural* rules a plan must satisfy to be executable —
/// cycle detection via Kahn's algorithm, referential integrity, reachability, branch completeness — and
/// this configuration deliberately does not duplicate any of them. What it adds is the bounding
/// <c>PlanValidator</c> has no opinion about: how large a definition may be, how many steps it may
/// contain, and how deeply sub-plans may nest.
/// </para>
/// <para>
/// <strong>Nesting depth is not the same control as <c>PlanConfiguration.MaxSubPlanDepth</c>.</strong>
/// That one is a runtime recursion guard applied while a plan executes. This one is an admission guard
/// applied before a plan is stored, walked over the persisted <c>ChildPlanId</c> chain. A definition
/// that exceeds the runtime limit would otherwise be accepted, stored, and fail only when run — which
/// reports a defect to whoever runs it rather than to whoever authored it.
/// </para>
/// <para>
/// <strong>Ceilings reject, they do not clamp.</strong> Every <c>Max…</c> value a caller can request —
/// timeouts, parallelism, retries — is compared against the matching ceiling here and the submission is
/// refused if it exceeds it. Silently lowering the request would be friendlier-looking and worse: a
/// caller who asked for a thirty-minute budget and received sixty seconds discovers the difference from
/// a timeout in production, with nothing in the response that explains it.
/// </para>
/// <code>
/// AppConfig.AI.WorkflowSubmission
/// ├── Enabled                  — Master toggle (default false)
/// ├── MaxRequestBytes          — Reject a request body larger than this, before deserialization
/// ├── MaxSteps                 — Maximum steps in a single submitted definition
/// ├── MaxEdges                 — Maximum edges in a single submitted definition
/// ├── MaxFanOutPerStep         — Maximum outbound edges from any one step
/// ├── MaxSubPlanNestingDepth   — Maximum depth of the persisted ChildPlanId chain
/// ├── MaxStringFieldLength     — Maximum length of any caller-supplied string field
/// ├── MaxPlanTimeout           — Ceiling on a requested whole-workflow wall-clock budget
/// ├── MaxStepTimeout           — Ceiling on a requested per-step timeout
/// ├── MaxParallelSteps         — Ceiling on a requested concurrent-step count
/// ├── MaxRetriesPerStep        — Ceiling on a requested per-step retry count
/// ├── MaxHumanGateTimeout      — Ceiling on how long a submitted step may park awaiting approval
/// ├── MaxTokensPerStep         — Ceiling on a requested LLM response-token count
/// ├── MaxTopK                  — Ceiling on a requested retrieval result count
/// ├── MaxStoredWorkflowsPerOwner — Cap on how many workflows one caller may keep stored
/// ├── RunRecordTtl             — How long a finished run stays readable
/// ├── MaxConcurrentRunsPerOwner — Cap on how many runs one caller may have in flight
/// ├── RunSweepInterval         — How often expired run records are reclaimed
/// └── MaxConcurrentDispatchedRuns — How many runs the host executes at once, across all callers
/// </code>
/// </remarks>
public class WorkflowSubmissionConfig
{
    /// <summary>
    /// Master toggle. When disabled (the default), the submission endpoint is not reachable and the
    /// host behaves identically to one with no workflow-submission concept at all.
    /// </summary>
    /// <value>Default: false</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum accepted size, in bytes, of a submission request body. Enforced before deserialization,
    /// so a hostile body costs a length check rather than a parse. Must be positive.
    /// </summary>
    /// <value>Default: 262144 (256 KiB)</value>
    public int MaxRequestBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Maximum number of steps a single submitted definition may declare. Bounds validation cost and
    /// the size of the persisted graph. Must be positive.
    /// </summary>
    /// <value>Default: 200</value>
    public int MaxSteps { get; set; } = 200;

    /// <summary>
    /// Maximum number of edges a single submitted definition may declare. Bounds the cost of the
    /// topological sort that <c>PlanValidator</c> runs, which is O(steps + edges). Must be positive.
    /// </summary>
    /// <value>Default: 400</value>
    public int MaxEdges { get; set; } = 400;

    /// <summary>
    /// Maximum number of outbound edges from any single step. A definition can respect
    /// <see cref="MaxEdges"/> in aggregate while still fanning out to hundreds of concurrent branches
    /// from one node, which is a concurrency cost the edge count alone does not bound.
    /// Must be positive.
    /// </summary>
    /// <value>Default: 32</value>
    public int MaxFanOutPerStep { get; set; } = 32;

    /// <summary>
    /// Maximum depth of the sub-plan chain reachable from a submitted definition, walked over the
    /// persisted <c>ChildPlanId</c> references with a visited set. A submission whose chain exceeds
    /// this is rejected at admission rather than failing when run. Must be positive.
    /// </summary>
    /// <value>Default: 3</value>
    public int MaxSubPlanNestingDepth { get; set; } = 3;

    /// <summary>
    /// Maximum length of any single caller-supplied string field in a submitted definition — step
    /// names, descriptions, prompts, and tool arguments. Bounds both the persisted row size and the
    /// cost of any downstream processing that treats these as text. Must be positive.
    /// </summary>
    /// <value>Default: 8192</value>
    public int MaxStringFieldLength { get; set; } = 8192;

    /// <summary>
    /// Ceiling on the whole-workflow wall-clock budget a submission may request. A submission asking
    /// for more is rejected. Bounds how long one caller can hold execution slots, which the step and
    /// edge counts do not — a two-step workflow can request an unbounded budget.
    /// </summary>
    /// <value>Default: 1 hour</value>
    public TimeSpan MaxPlanTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Ceiling on the per-step timeout a submission may request. Distinct from
    /// <see cref="MaxPlanTimeout"/> because a single step blocking for the entire plan budget starves
    /// every step that would otherwise have run beside it.
    /// </summary>
    /// <value>Default: 15 minutes</value>
    public TimeSpan MaxStepTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Ceiling on the concurrent-step count a submission may request. This is the practical limit on
    /// how much host capacity one submission can consume at any instant, and therefore the control
    /// that most directly bounds a caller's effect on everyone else's latency.
    /// </summary>
    /// <value>Default: 16</value>
    public int MaxParallelSteps { get; set; } = 16;

    /// <summary>
    /// Ceiling on the retry count a submission may request for any one step. Each retry re-runs the
    /// step's full cost — an inference call, a tool invocation — so this multiplies, rather than adds
    /// to, the workflow's ceiling cost.
    /// </summary>
    /// <value>Default: 10</value>
    public int MaxRetriesPerStep { get; set; } = 10;

    /// <summary>
    /// Ceiling on how long a submitted human-gate step may park awaiting approval.
    /// </summary>
    /// <remarks>
    /// A parked gate holds a run open and a pending approval in an operator's queue. Left unbounded, a
    /// submission could enqueue approvals that never expire, so the queue grows without limit and no
    /// operator can tell which entries are still meaningful.
    /// </remarks>
    /// <value>Default: 24 hours</value>
    public TimeSpan MaxHumanGateTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Ceiling on the response-token count a submitted LLM step may request. Tokens are the direct
    /// unit of inference spend, so this is the per-step cost limit the graph-size caps cannot express:
    /// a one-step workflow can otherwise request an unbounded completion.
    /// </summary>
    /// <value>Default: 32768</value>
    public int MaxTokensPerStep { get; set; } = 32768;

    /// <summary>
    /// Ceiling on the result count a submitted retrieval step may request. Bounds both the retrieval
    /// work performed and the volume of retrieved text that subsequently enters a model's context.
    /// </summary>
    /// <value>Default: 100</value>
    public int MaxTopK { get; set; } = 100;

    /// <summary>
    /// Maximum number of workflows one caller may have stored at once. A submission that would exceed
    /// it is refused until the caller removes some.
    /// </summary>
    /// <remarks>
    /// Every other cap here bounds a <em>single</em> submission, and the rate limiter bounds the
    /// <em>rate</em> of submissions. Neither bounds the total. Without this, a caller staying politely
    /// within both can still accumulate storage without limit, one well-formed request at a time —
    /// the aggregate being the one quantity an otherwise carefully-capped surface left open.
    /// </remarks>
    /// <value>Default: 500</value>
    public int MaxStoredWorkflowsPerOwner { get; set; } = 500;

    /// <summary>
    /// How long a finished run stays readable before it is reclaimed.
    /// </summary>
    /// <remarks>
    /// The clock starts when the run reaches a terminal state, not when it was accepted, so a run that
    /// waited a long time in the queue still gets its full readable window afterwards. A run that has
    /// not finished is never reclaimed at all — expiring one a caller is still polling would make it
    /// silently disappear rather than report an outcome.
    /// </remarks>
    /// <value>Default: 1 hour</value>
    public TimeSpan RunRecordTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of runs one caller may have queued or executing at once.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MaxStoredWorkflowsPerOwner"/>, which bounds stored definitions: this
    /// bounds work in flight. A caller within the storage quota can otherwise start every workflow it
    /// owns simultaneously, and the per-workflow parallelism ceiling multiplies by that count.
    /// </remarks>
    /// <value>Default: 10</value>
    public int MaxConcurrentRunsPerOwner { get; set; } = 10;

    /// <summary>
    /// How often finished runs past their <see cref="RunRecordTtl"/> are reclaimed.
    /// </summary>
    /// <remarks>
    /// Separate from the retention window rather than derived from it. The two answer different
    /// questions — how long a caller may read a finished run, and how promptly the host gives that
    /// memory back — and an operator who lengthens the readable window rarely means to make sweeps
    /// correspondingly rare. Only terminal records are ever reclaimed, so this cannot shorten the life
    /// of work still in flight.
    /// </remarks>
    /// <value>Default: 5 minutes</value>
    public TimeSpan RunSweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many runs the host executes at once, across all callers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="MaxConcurrentRunsPerOwner"/>, which bounds what one caller may have
    /// <em>accepted</em>. This bounds what the host actually executes. At 1 the dispatcher is strictly
    /// serial and a single long workflow delays every other caller's — which makes the per-owner cap
    /// read as a concurrency guarantee the host does not provide.
    /// </para>
    /// <para>
    /// <strong>This is not a fairness mechanism.</strong> Runs are dispatched in the order they were
    /// accepted, so a caller that queues many runs at once still occupies the slots ahead of a caller
    /// that queues one. Per-caller fair scheduling is a separate piece of work; raising this reduces
    /// how long anyone waits but does not decide who waits.
    /// </para>
    /// </remarks>
    /// <value>Default: 4</value>
    public int MaxConcurrentDispatchedRuns { get; set; } = 4;
}

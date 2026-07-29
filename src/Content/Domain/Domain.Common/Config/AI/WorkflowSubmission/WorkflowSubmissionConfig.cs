namespace Domain.Common.Config.AI.WorkflowSubmission;

/// <summary>
/// Root configuration for the workflow-submission surface — the host's ability to accept an
/// externally-authored workflow definition over HTTP, validate it, and persist it as a plan the
/// caller can later run. Bound from <c>AppConfig:AI:WorkflowSubmission</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default.</strong> A submitted workflow is untrusted, externally-authored input that
/// ultimately drives model inference, tool invocation and retrieval on the host's credentials. A fresh
/// consumer pays no cost and exposes no surface until they deliberately opt in — the same posture as
/// <see cref="BundleExecution.BundleExecutionConfig"/>, for the same reason.
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
/// <code>
/// AppConfig.AI.WorkflowSubmission
/// ├── Enabled                  — Master toggle (default false)
/// ├── MaxRequestBytes          — Reject a request body larger than this, before deserialization
/// ├── MaxSteps                 — Maximum steps in a single submitted definition
/// ├── MaxEdges                 — Maximum edges in a single submitted definition
/// ├── MaxFanOutPerStep         — Maximum outbound edges from any one step
/// ├── MaxSubPlanNestingDepth   — Maximum depth of the persisted ChildPlanId chain
/// ├── MaxStringFieldLength     — Maximum length of any caller-supplied string field
/// └── AllowInlineSubPlans      — Whether a definition may embed a child definition inline (default false)
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
    /// Whether a submitted definition may embed a child workflow inline rather than referencing a
    /// previously-submitted one by id.
    /// </summary>
    /// <remarks>
    /// Off by default, deliberately. An inline child makes the request body recursive, which turns
    /// <see cref="MaxSubPlanNestingDepth"/> from a graph walk into a parser concern — the depth must
    /// then be bounded during deserialization, before the object exists to inspect. Reference-only
    /// submission keeps every child a separately-admitted, separately-owned plan, so the depth walk
    /// happens over persisted rows the caller has already been authorized for.
    /// </remarks>
    /// <value>Default: false</value>
    public bool AllowInlineSubPlans { get; set; }
}

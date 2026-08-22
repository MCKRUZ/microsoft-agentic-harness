using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// The single ordered chain that decides whether a tool call may proceed. Every execution path that
/// can reach a tool calls this and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A tool call can reach a tool through five different execution
/// paths — the agent turn, the Execution API, and the plan engine's tool, LLM and retrieval steps.
/// Each one used to ask the same admission questions by hand, in its own copy of the sequence,
/// obtaining the gates three different ways. A gate was added to one path and forgotten on the others
/// four separate times; the last of those left a consumer's own safety rule live in a chat turn and
/// absent from the identical call issued from a plan. Adding a gate here adds it to every path at
/// once, so there is no longer a set of other paths to forget.
/// </para>
/// <para>
/// <strong>The order is the safety argument, not an implementation detail.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="IAgentToolAuthorizationGate"/> — whether the executing agent's workload identity is
/// permitted this tool at all. First because it is the most fundamental access question and the
/// cheapest to answer, and because the next stage can escalate to a human: asking a person to
/// approve a call that RBAC refuses anyway is a wasted interruption that also teaches operators to
/// approve calls which were never permitted.
/// </description></item>
/// <item><description>
/// <see cref="IToolInvocationGovernor"/> — permission, capability, envelope and declarative policy.
/// </description></item>
/// <item><description>
/// <see cref="IToolClassificationGate"/> — the data sensitivity of what the call touches.
/// </description></item>
/// <item><description>
/// <see cref="IToolCallObserverChain"/> — the host's own rules, <em>last of the access gates</em>, so
/// that by the time consumer-authored code is consulted every question about whether the agent may use
/// the tool at all has been settled. An observer can therefore only make the outcome stricter; it can
/// never resurrect a call the governor denied, overrule the capability envelope, or bypass a plugin's
/// deny list. That is the whole reason it is safe to let consumer code into this path.
/// </description></item>
/// <item><description>
/// <see cref="IProgressEvaluator"/> — the loop guard, second-to-last because it is a stage that
/// <strong>mutates</strong> state. Asking it about a call is also what records that call, so it must
/// only ever be asked about calls that have cleared everything else. Running it earlier let blocked
/// calls reset the no-progress counter, and an agent retrying a blocked call with a slightly different
/// argument each time never tripped the guard it was spinning against.
/// </description></item>
/// <item><description>
/// <see cref="ICallOnceGate"/> — durable call-once enforcement, last of all, and for the same
/// "asking is claiming" reason as the loop guard: it too only makes sense to ask about a call that
/// has cleared everything else. Unlike the loop guard it carries no in-process state — the claim is
/// durable — so nothing here is reset per turn.
/// </description></item>
/// </list>
/// <para>
/// Any change that does not preserve that order is wrong, and
/// <c>ToolCallAdmissionPipelineTests</c> pins it in a single assertion.
/// </para>
/// <para>
/// <strong>Do not try to fix the loop guard's coupling by splitting it.</strong> Making it answer
/// without recording, so its position here stopped mattering, is an obvious-looking cleanup and has
/// been proposed as one. It is a defect — see <see cref="IProgressEvaluator"/>. The coupling is what
/// makes the guard work; this position is what makes the coupling safe.
/// </para>
/// </remarks>
public interface IToolCallAdmissionPipeline
{
    /// <summary>
    /// Runs the admission chain for one tool call.
    /// </summary>
    /// <param name="request">The call being admitted.</param>
    /// <param name="cancellationToken">
    /// Cancels the admission, including any approval a gate escalates to a human. Cancellation
    /// propagates rather than becoming a verdict — an abandoned call is not a policy decision.
    /// </param>
    /// <returns>
    /// The verdict. A refusal always carries a caller-facing message; an allow may carry an
    /// instruction to redact the tool's output, which the caller applies through
    /// <see cref="ApplyOutputPolicy"/>.
    /// </returns>
    ValueTask<ToolCallAdmission> AdmitAsync(ToolCallAdmissionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Applies whatever output policy the admission carried, after the tool has run.
    /// </summary>
    /// <param name="admission">The verdict returned by <see cref="AdmitAsync"/> for this same call.</param>
    /// <param name="toolName">The tool that produced <paramref name="result"/>.</param>
    /// <param name="result">The tool's raw result.</param>
    /// <returns>
    /// <paramref name="result"/>'s text, run unconditionally through the general-purpose sanitizer
    /// (injection payloads, invisible characters, exfiltration URLs) — the same treatment whether or not
    /// the admission carried a redact verdict; a redact verdict routes through
    /// <see cref="IToolClassificationGate.RedactResult"/>, which applies that same sanitizer rather than
    /// a distinct sensitivity-aware scrub. A structured (non-text) result is returned unchanged either
    /// way — the sanitizer operates on free text. One MCP-specific shape currently falls into that
    /// unchanged bucket even though it does carry embedded text — a serialized <c>CallToolResult</c>
    /// (structured content or protocol metadata present) — tracked separately as a known gap in
    /// <c>ToolResultText</c>, the type this method delegates the shape-preserving sanitize to.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Admission is not purely a pre-call decision: a classified asset can be allowed through and have
    /// its output scrubbed instead of being refused outright. Keeping that second half here means a
    /// caller never has to hold the classification gate itself, and cannot forget to consult it.
    /// </para>
    /// <para>
    /// <strong>The sanitize pass is unconditional, not gated on <see cref="ToolCallAdmission.RedactsOutput"/>.</strong>
    /// A tool's result is attacker-influenced content on this method's one caller — the agent turn,
    /// which hands the result straight to the model — regardless of whether the classification gate ever
    /// flagged this particular call for redaction. Gating the sanitizer on that flag would leave every
    /// unclassified or intentionally-unredacted tool call with no injection-scrubbing pass at all (#469),
    /// unlike this pipeline's other execution paths, which already sanitize unconditionally.
    /// </para>
    /// </remarks>
    object? ApplyOutputPolicy(ToolCallAdmission admission, string toolName, object? result);

    /// <summary>
    /// Applies the admission's output policy to a result that must leave as <em>text</em>, reporting
    /// whether it produced usable text.
    /// </summary>
    /// <param name="admission">The verdict returned by <see cref="AdmitAsync"/> for this same call.</param>
    /// <param name="toolName">The tool that produced <paramref name="content"/>.</param>
    /// <param name="content">The tool's raw text.</param>
    /// <param name="result">
    /// The text to emit: <paramref name="content"/> unchanged when no redaction was required, or the
    /// scrubbed text. Null when the method returns <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when a redaction was required but did not produce text, in which case
    /// the caller must <strong>withhold</strong> the result rather than emit the original.
    /// </returns>
    /// <remarks>
    /// Separate from <see cref="ApplyOutputPolicy"/> because the two callers want different things
    /// from the same verdict. The agent turn hands the model structured results and passes them
    /// through untouched, so it wants the object form. The plan step and the Execution API emit text
    /// across a boundary, and for them a non-text answer means a gate did something unexpected — on a
    /// redaction path that is a reason to withhold, not to shrug. Both used to implement that
    /// fail-closed rule themselves, in two copies, which is one copy more than a rule like this
    /// survives.
    /// </remarks>
    bool TryApplyTextOutputPolicy(
        ToolCallAdmission admission, string toolName, string? content, out string? result);

    /// <summary>
    /// Reports what happened when a call this pipeline approved was actually carried out, closing
    /// the approval loop for whichever approver granted it.
    /// </summary>
    /// <param name="admission">The verdict returned by <see cref="AdmitAsync"/> for this same call.</param>
    /// <param name="report">The outcome to report.</param>
    /// <param name="reportedBy">
    /// A stable identifier for the calling site (e.g. <c>"direct-invocation"</c>,
    /// <c>"agent-turn"</c>, <c>"plan-executor"</c>) — the pipeline is one shared, scoped instance
    /// reached from all three, so it cannot infer this from anything of its own; the caller knows
    /// which of the three it is and must say so. Carried onto the audit record so a future auditor
    /// can tell which raising sites implement execution reporting and which don't.
    /// </param>
    /// <remarks>
    /// A no-op when <paramref name="admission"/> carries no <see cref="ToolCallAdmission.ApprovedCall"/>
    /// — most calls need no human approval, and there is nothing to report a loop closing on.
    /// Never throws: delegates to <see cref="IApprovalExecutionReporter"/>, whose own contract is
    /// the same must-not-throw guarantee, for the same reason — this runs after the call already
    /// completed.
    /// </remarks>
    ValueTask ReportExecutionAsync(
        ToolCallAdmission admission, ToolExecutionReport report, string reportedBy, CancellationToken cancellationToken);

    /// <summary>
    /// The turn's governance trace: every decision the chain's stages recorded, as one record.
    /// </summary>
    /// <remarks>
    /// Surfaced here rather than at each caller. Every stage writes its own decisions and escalation
    /// codes to one shared <see cref="IGovernanceTraceRecorder"/>, so this is a single snapshot rather
    /// than a composition — but knowing that the turn has exactly one trail, and which type holds it,
    /// is knowledge about how the stages fit together, which is this type's job and nobody else's. Two
    /// callers previously assembled the trace by hand from two injected stages.
    /// </remarks>
    Domain.AI.Governance.GovernanceTrace GetTrace();

    /// <summary>
    /// Clears the per-turn state the chain accumulates — the governance trail and the loop guard's
    /// call history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once at the start of a turn or a single invocation. Resetting the whole chain in one call
    /// is deliberate: the stateful parts were previously reset independently at each arming site, and a
    /// site that reset one but not the other carried a turn's history into the next.
    /// </para>
    /// <para>
    /// <strong>Deliberately does not touch <see cref="ICallOnceGate"/>.</strong> That stage carries no
    /// per-turn, in-process state to clear — its claim is durable, keyed by conversation, and surviving
    /// exactly this reset (and everything else that resets per turn, per run, or per host) is the whole
    /// point of it.
    /// </para>
    /// </remarks>
    void Reset();
}

/// <summary>
/// One tool call, as the admission chain sees it.
/// </summary>
/// <param name="ToolName">
/// The tool or plan capability being admitted. This is also the keyed-DI key for a real tool, and a
/// well-known constant for a plan capability such as <c>llm_call</c> or <c>rag_retrieval</c>.
/// </param>
/// <param name="Arguments">
/// The concrete call arguments, when the caller has them. They let an approval verdict describe the
/// specific invocation to a human, let argument-conditioned policy rules match, let the classification
/// gate resolve which asset the call touches, and are what a consumer's rule inspects to make an
/// argument-sensitive decision. Omitting them where they exist does not fail closed — it silently
/// narrows every one of those checks to the tool name.
/// </param>
/// <param name="CountsTowardLoopDetection">
/// Whether this call is part of a sequence the loop guard should be counting. True only on the agent
/// turn, which is the only caller that issues a repeatable series of tool calls within one unit of
/// work. A single Execution API invocation has no sequence to evaluate, and a plan DAG has its own
/// retry and recovery — counting either would be machinery that could not fire, or could only misfire.
/// </param>
/// <param name="CompositionTaint">
/// The tool-composition findings that implicate <see cref="ToolName"/> as a sink, stamped at agent
/// build time by <c>ToolChainBuilder</c> and carried here by <c>GovernedAIFunction</c> — the only
/// carrier that reaches every execution path, since neither <c>AgentExecutionContext</c> type is both
/// populated with the tool set and reachable from a freshly-scoped plan step. Null for every call this
/// wrapper did not stamp, which the governor reads identically to "no findings". See
/// <c>ToolInvocationGovernor.RequiresApprovalForToolComposition</c>.
/// </param>
public sealed record ToolCallAdmissionRequest(
    string ToolName,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    bool CountsTowardLoopDetection = false,
    ToolCompositionTaint? CompositionTaint = null);

/// <summary>
/// The admission chain's verdict for one tool call.
/// </summary>
/// <remarks>
/// <strong>Constructible only through the factories below, and that is load-bearing.</strong> Every
/// caller acts on <see cref="DeniedMessage"/> directly — the agent turn returns it to the model in
/// place of the tool result — so a refusal carrying no text would surface as an <em>empty successful
/// result</em>, which an agent reads as the tool having run and returned nothing. A public
/// constructor would let a consumer's own <see cref="IToolCallAdmissionPipeline"/> produce exactly
/// that. Keeping the shape unreachable is cheaper than five defensive fallbacks that each have to
/// remember why they exist.
/// </remarks>
public sealed record ToolCallAdmission
{
    private static readonly ToolCallAdmission AllowedDecision = new(true, null, false, null);
    private static readonly ToolCallAdmission AllowedRedactingDecision = new(true, null, true, null);

    private ToolCallAdmission(bool isAllowed, string? deniedMessage, bool redactsOutput, ApprovedCall? approvedCall)
    {
        IsAllowed = isAllowed;
        DeniedMessage = deniedMessage;
        RedactsOutput = redactsOutput;
        ApprovedCall = approvedCall;
    }

    /// <summary>Whether the call may proceed to the tool.</summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// The caller-facing refusal text: never null or blank when <see cref="IsAllowed"/> is false, and
    /// always null when it is true. Deliberately uninformative about which stage refused — see
    /// <see cref="Domain.AI.Governance.GovernanceDenials"/>.
    /// </summary>
    public string? DeniedMessage { get; }

    /// <summary>
    /// Whether the tool's output must be scrubbed before it leaves. Only ever true on an allow.
    /// </summary>
    public bool RedactsOutput { get; }

    /// <summary>
    /// The approval that permitted this call, when a human approved it. Null on every other allow
    /// and on every refusal. Set via <see cref="WithApproval"/>, never in the constructor directly,
    /// so the two cached singletons below never need a variant for every approval.
    /// </summary>
    public ApprovedCall? ApprovedCall { get; private init; }

    /// <summary>The call may proceed and its output is returned as-is.</summary>
    public static ToolCallAdmission Allow() => AllowedDecision;

    /// <summary>The call may proceed, but its output must be scrubbed before it leaves.</summary>
    public static ToolCallAdmission AllowWithOutputRedaction() => AllowedRedactingDecision;

    /// <summary>
    /// Returns this allow, stamped with the human approval that permitted it. Preserves
    /// <see cref="RedactsOutput"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This admission is a refusal.</exception>
    public ToolCallAdmission WithApproval(ApprovedCall call)
    {
        if (!IsAllowed)
            throw new InvalidOperationException("Cannot attach an approval to a refused admission.");

        return this with { ApprovedCall = call };
    }

    /// <summary>The call is refused, carrying the caller-facing text.</summary>
    /// <param name="deniedMessage">
    /// The refusal text. Must contain something a caller can surface — blank is rejected as well as
    /// null, because whitespace reaches a model as indistinguishable from an empty result.
    /// </param>
    public static ToolCallAdmission Deny(string deniedMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deniedMessage);
        return new ToolCallAdmission(false, deniedMessage, false, null);
    }
}

/// <summary>The execution outcome to report for a call the admission chain approved.</summary>
/// <param name="Status">What happened.</param>
/// <param name="FailureReason">
/// The tool's own raw, untreated failure text when <paramref name="Status"/> is
/// <see cref="EscalationExecutionStatus.Failed"/>. Sanitized, redacted, and bounded by
/// <see cref="Services.Governance.ToolCallAdmissionPipeline.ReportExecutionAsync"/> before it reaches
/// the audit trail, the approver, or the failure memory — callers must pass the raw text, not a
/// pre-treated copy, or that treatment runs twice on an already-safe string for no benefit.
/// </param>
/// <param name="NotExecutedReason">Why the call never ran, when <paramref name="Status"/> is <see cref="EscalationExecutionStatus.NeverExecuted"/>.</param>
/// <param name="ToolName">
/// The tool that produced <paramref name="FailureReason"/>, passed to the sanitizer as context. Null
/// is safe — the sanitizer's tool-name parameter is optional context, not a required key.
/// </param>
public readonly record struct ToolExecutionReport(
    EscalationExecutionStatus Status,
    string? FailureReason,
    EscalationNotExecutedReason? NotExecutedReason,
    string? ToolName = null);

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
/// <see cref="IProgressEvaluator"/> — the loop guard, last of all because it is the only stage that
/// <strong>mutates</strong> state. It records the call's signature and resets the no-progress counter,
/// so it must only ever count calls that actually reached the tool. Running it earlier let blocked
/// calls reset the counter, and an agent retrying a blocked call with a slightly different argument
/// each time never tripped the guard it was spinning against.
/// </description></item>
/// </list>
/// <para>
/// Any change that does not preserve that order is wrong, and
/// <c>ToolCallAdmissionPipelineTests</c> pins it in a single assertion.
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
    /// <paramref name="result"/> unchanged unless the admission carried a redact verdict, in which case
    /// the scrubbed result.
    /// </returns>
    /// <remarks>
    /// Admission is not purely a pre-call decision: a classified asset can be allowed through and have
    /// its output scrubbed instead of being refused outright. Keeping that second half here means a
    /// caller never has to hold the classification gate itself, and cannot forget to consult it.
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
    /// The turn's governance trace: every decision the chain's stages recorded, as one record.
    /// </summary>
    /// <remarks>
    /// Composed here rather than at each caller. The trace is the governor's decision list with any
    /// escalation reason codes the loop guard raised folded in, and knowing that those two belong in
    /// one record is knowledge about how the stages compose — which is this type's job and nobody
    /// else's. Two callers previously assembled it by hand from two injected stages.
    /// </remarks>
    Domain.AI.Governance.GovernanceTrace GetTrace();

    /// <summary>
    /// Clears the per-turn state the chain's stages accumulate — the governor's decision trace and the
    /// loop guard's call history.
    /// </summary>
    /// <remarks>
    /// Called once at the start of a turn or a single invocation. Resetting the whole chain in one call
    /// is deliberate: the two stateful stages were previously reset independently at each arming site,
    /// and a site that reset one but not the other carried a turn's history into the next.
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
public sealed record ToolCallAdmissionRequest(
    string ToolName,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    bool CountsTowardLoopDetection = false);

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
    private static readonly ToolCallAdmission AllowedDecision = new(true, null, false);
    private static readonly ToolCallAdmission AllowedRedactingDecision = new(true, null, true);

    private ToolCallAdmission(bool isAllowed, string? deniedMessage, bool redactsOutput)
    {
        IsAllowed = isAllowed;
        DeniedMessage = deniedMessage;
        RedactsOutput = redactsOutput;
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

    /// <summary>The call may proceed and its output is returned as-is.</summary>
    public static ToolCallAdmission Allow() => AllowedDecision;

    /// <summary>The call may proceed, but its output must be scrubbed before it leaves.</summary>
    public static ToolCallAdmission AllowWithOutputRedaction() => AllowedRedactingDecision;

    /// <summary>The call is refused, carrying the caller-facing text.</summary>
    /// <param name="deniedMessage">
    /// The refusal text. Must contain something a caller can surface — blank is rejected as well as
    /// null, because whitespace reaches a model as indistinguishable from an empty result.
    /// </param>
    public static ToolCallAdmission Deny(string deniedMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deniedMessage);
        return new ToolCallAdmission(false, deniedMessage, false);
    }
}

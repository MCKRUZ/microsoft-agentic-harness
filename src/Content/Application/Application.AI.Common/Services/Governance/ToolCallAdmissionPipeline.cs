using System.Collections.ObjectModel;
using System.Text.Json;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolCallAdmissionPipeline"/>: runs the six admission stages in the one order
/// that is safe, and owns everything that follows from that ordering.
/// </summary>
/// <remarks>
/// <para>
/// The ordering rationale lives on <see cref="IToolCallAdmissionPipeline"/> and is not repeated here.
/// What this type adds beyond sequencing:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A caller's absent arguments become an empty dictionary before any stage that requires one, so a
/// consumer-authored rule can read arguments without a null check. "The caller had none" and "the call
/// had none" are the same thing to a rule.
/// </description></item>
/// <item><description>
/// The loop guard's call signature is computed here and computed lazily, so a call the earlier stages
/// refuse never pays for serialising its arguments.
/// </description></item>
/// <item><description>
/// A refusal always carries text. Every stage's own refusal factory already requires a message, so the
/// fallback is unreachable defence — but it resolves to the one canonical denial rather than to text
/// naming the stage, because a caller must not be able to tell from the message which gate refused.
/// </description></item>
/// </list>
/// </remarks>
public sealed class ToolCallAdmissionPipeline : IToolCallAdmissionPipeline
{
    // Unit-separator (U+001F) cannot appear in a JSON-serialised value, so distinct argument sets
    // cannot collide into the same joined signature. Built from a char code to keep the source ASCII.
    private static readonly string ArgPairSeparator = ((char)0x1F).ToString();

    // Shared across every call and handed to consumer-authored code, so it is a ReadOnlyDictionary
    // rather than a bare Dictionary — a rule could otherwise downcast it and mutate it for everyone.
    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        ReadOnlyDictionary<string, object?>.Empty;

    private readonly IAgentToolAuthorizationGate _authorizationGate;
    private readonly IToolInvocationGovernor _governor;
    private readonly IToolClassificationGate _classificationGate;
    private readonly IToolCallObserverChain _observers;
    private readonly IProgressEvaluator _progressEvaluator;
    private readonly ICallOnceGate _callOnceGate;
    private readonly IGovernanceTraceRecorder _trace;
    private readonly IApprovalExecutionReporter _executionReporter;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _redactionFilter;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ToolCallAdmissionPipeline> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCallAdmissionPipeline"/> class.</summary>
    /// <param name="authorizationGate">
    /// Stage 1 — whether the executing agent identity is permitted this tool. Required rather than
    /// optional, on the same argument as the two stages below: it reports its own off state by
    /// admitting, so an absent gate and a switched-off gate would be indistinguishable at runtime.
    /// </param>
    /// <param name="governor">Stage 2 — permission, capability, envelope and declarative policy.</param>
    /// <param name="classificationGate">
    /// Stage 3 — data sensitivity. Required rather than optional: it is registered unconditionally and
    /// reports its own off state internally, so an absent one would be indistinguishable at runtime from
    /// a host that turned classification off, and only one of those is safe.
    /// </param>
    /// <param name="observers">
    /// Stage 4 — the host's own rules. Required for the same reason: an absent chain and a chain with
    /// nothing in it are indistinguishable at runtime.
    /// </param>
    /// <param name="progressEvaluator">Stage 5 — the loop guard.</param>
    /// <param name="callOnceGate">
    /// Stage 6 — durable call-once enforcement, after the loop guard for the same reason: it too
    /// only makes sense to ask about a call that has cleared every access question already.
    /// </param>
    /// <param name="options">
    /// Supplies the tool-output ceiling — see <see cref="OutputCeiling"/> for why it reuses the
    /// existing per-result limit rather than adding a setting of its own. Read per call through
    /// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> rather than captured, so a hot-reloaded
    /// ceiling takes effect on the next tool result instead of at the next process start.
    /// </param>
    /// <param name="trace">The turn's governance trail, which the stages write to and this type snapshots.</param>
    /// <param name="executionReporter">
    /// Closes the approval loop for a call this pipeline approved — see <see cref="ReportExecutionAsync"/>.
    /// </param>
    /// <param name="sanitizer">
    /// Two independent uses. Paired with <paramref name="redactionFilter"/>, prepares a failed call's
    /// raw failure text for reporting — see <see cref="ReportExecutionAsync"/> and
    /// <see cref="ReportedFailureText.PrepareForReporting"/>. On its own, also the unconditional
    /// injection-scrubber every plain-allow tool result is run through in
    /// <see cref="ApplyOutputPolicy"/> — see #469; that path never reaches
    /// <paramref name="redactionFilter"/> at all.
    /// </param>
    /// <param name="redactionFilter">Scrubs known secret patterns from a failed call's reported text.</param>
    /// <param name="logger">Records a redaction that could not be applied.</param>
    public ToolCallAdmissionPipeline(
        IAgentToolAuthorizationGate authorizationGate,
        IToolInvocationGovernor governor,
        IToolClassificationGate classificationGate,
        IToolCallObserverChain observers,
        IProgressEvaluator progressEvaluator,
        ICallOnceGate callOnceGate,
        IGovernanceTraceRecorder trace,
        IApprovalExecutionReporter executionReporter,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        IOptionsMonitor<AppConfig> options,
        ILogger<ToolCallAdmissionPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        ArgumentNullException.ThrowIfNull(authorizationGate);
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(classificationGate);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(progressEvaluator);
        ArgumentNullException.ThrowIfNull(callOnceGate);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(executionReporter);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(redactionFilter);
        ArgumentNullException.ThrowIfNull(logger);

        _authorizationGate = authorizationGate;
        _governor = governor;
        _classificationGate = classificationGate;
        _observers = observers;
        _progressEvaluator = progressEvaluator;
        _callOnceGate = callOnceGate;
        _trace = trace;
        _executionReporter = executionReporter;
        _sanitizer = sanitizer;
        _redactionFilter = redactionFilter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ToolCallAdmission> AdmitAsync(
        ToolCallAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var toolName = request.ToolName;
        var arguments = request.Arguments ?? EmptyArguments;

        // 1 — per-agent tool authorization, ahead of everything else. It is the cheapest stage (a
        // dictionary lookup once an identity is in hand) and the most fundamental question — whether
        // this agent may use this tool at all — so nothing more expensive should run before it. The
        // ordering is load-bearing rather than merely tidy: the governor below can escalate to a human
        // for approval, and asking a person to approve a call that RBAC refuses anyway is both a wasted
        // interruption and a way to train operators to approve calls that were never permitted.
        //
        // Applied to capability gates (null arguments) as well as real tool calls. Unlike the
        // classification gate below, there is nothing here that degrades without arguments: the
        // question is about the caller's identity and the operation's name, both of which are always
        // present. Exempting the plan engine's llm_call and rag_retrieval would leave an agent barred
        // from a tool able to reach equivalent capability through a plan step.
        var authorization = await _authorizationGate
            .EvaluateAsync(toolName, cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsAllowed)
        {
            // Put the refusal on the turn's trace — see IGovernanceTraceRecorder.RecordDownstreamBlock
            // for why this is routed through the recorder rather than skipped.
            _trace.RecordDownstreamBlock(toolName, "denied by per-agent tool authorization");
            return Refuse(authorization.DeniedMessage, toolName);
        }

        // 2 — the built-in governor. Arguments are passed through as the caller supplied them,
        // null included: the governor distinguishes "no arguments were available" from "the call had
        // none", and narrows its argument-conditioned rules accordingly.
        var decision = await _governor
            .AuthorizeAsync(toolName, cancellationToken, request.Arguments, request.CompositionTaint)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Refuse(decision.DeniedMessage, toolName);

        // 3 — data classification, for calls that have a data surface to classify. A block refuses the
        // call outright; a redact verdict lets it run and scrubs the output afterwards, which is why
        // the verdict survives past this stage.
        //
        // A request carrying NO arguments is a capability gate, not a tool call — "may this run call a
        // model at all", "may it retrieve at all" — and there is nothing for an asset resolver to
        // resolve. Running the gate anyway would not classify anything; it would resolve to Unknown
        // and hand the decision to the host's unknown-asset policy, which is a verdict about the
        // absence of information rather than about the call. A host that hardens that policy to Block
        // would then fail every LLM-call and retrieval step in every plan.
        //
        // This is a property of the REQUEST, not of the calling path, so it stays uniform: every
        // caller with arguments is classified and every caller without is not. A tool call always has
        // an argument dictionary even when it is empty — only the two plan capability gates pass null
        // — so no real tool call can slip through this.
        var classification = ClassificationVerdict.Allow();
        if (request.Arguments is not null)
        {
            classification = await _classificationGate
                .EvaluateAsync(toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
            if (classification.Outcome == ClassificationGateOutcome.Block)
                return Refuse(classification.BlockedMessage, toolName);
        }

        // 4 — the host's own rules, last of the access gates.
        if (_observers.HasObservers)
        {
            var observed = await _observers
                .EvaluateAsync(toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
            if (!observed.IsAllowed)
                return Refuse(observed.DeniedMessage, toolName);
        }

        // 5 — the loop guard, and only for callers that issue a sequence. Asking it about a call is
        // also what records that call, so it must be asked LAST, below every path above that returns
        // a refusal. Splitting it so that stopped mattering is a defect, not a cleanup — see
        // IProgressEvaluator for why, and ProgressEvaluatorConcurrencyTests for the measurement.
        if (request.CountsTowardLoopDetection)
        {
            var verdict = _progressEvaluator.Evaluate(
                toolName, () => ComputeArgumentsSignature(request.Arguments));
            if (verdict.ShouldHalt)
                return Refuse(verdict.HaltMessage, toolName);
        }

        // 6 — durable call-once enforcement, last of all: a different kind of question ("has this
        // call already happened", not "may this call happen") that only makes sense to ask once
        // every access question above has already cleared. Unlike the loop guard, this gate carries
        // no in-process state to be careful about — see ICallOnceGate's remarks.
        var callOnce = await _callOnceGate.EvaluateAsync(toolName, cancellationToken).ConfigureAwait(false);
        if (!callOnce.IsAllowed)
        {
            // Recorded here, not inside CallOnceGate itself, mirroring stage 1's own trace write —
            // an unreported denial is indistinguishable from an unenforced rule to governance
            // reporting, the dashboard, and the audit.
            _trace.RecordDownstreamBlock(toolName, "denied by call-once enforcement");
            return Refuse(callOnce.DeniedMessage, toolName);
        }

        var admission = classification.Outcome == ClassificationGateOutcome.RedactOutput
            ? ToolCallAdmission.AllowWithOutputRedaction()
            : ToolCallAdmission.Allow();

        // Stamp the approval that permitted this call, when there was one, so the caller can
        // close the loop on it after the tool runs. An approval only ever loosens THIS gate — a
        // later gate above still refused the call outright above, so a call that reaches here
        // with an approval genuinely proceeded because a human said yes.
        return decision.ApprovedCall is { } call ? admission.WithApproval(call) : admission;
    }

    /// <summary>
    /// Appended where a tool result is cut to <see cref="OutputCeiling"/>, so a truncation is visible
    /// to the model rather than reading as the tool having returned exactly that much.
    /// </summary>
    public const string OutputTruncationMarker = "\n[tool output truncated]";

    /// <summary>
    /// How much beyond <see cref="OutputCeiling"/> is kept while sanitizing and redacting, so a secret
    /// or an injection pattern straddling the ceiling stays inside the scanned region rather than being
    /// sliced in half — removed again by the final cut. See
    /// <see cref="ToolResultText.PreCutForScan(object?, int, int, string)"/> for the full rationale and
    /// the residual risk it accepts.
    /// </summary>
    /// <remarks>
    /// Sized generously against the longest thing the sanitizers look for — connection strings and
    /// PEM-armoured keys run to a few kilobytes — because the cost of being wrong in one direction is a
    /// few spare kilobytes scanned, and in the other it is an unredacted secret on the wire. This value,
    /// and the pre-cut it sizes, used to live privately on <c>DirectToolInvoker</c> alone; every caller
    /// of THIS pipeline needs the same protection against scanning an unbounded result, so it moved here
    /// — the one place that already owns <see cref="OutputCeiling"/> (#487). Not a claim about every
    /// path that reaches a tool result — see #544 for one known bypass.
    /// </remarks>
    internal const int ScrubOverlapMargin = 8 * 1024;

    /// <summary>
    /// The maximum characters of free text a single tool result may contribute to the context window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reuses <c>AI.ContextManagement.ToolResultStorage.PerResultCharLimit</c> rather than introducing
    /// a second number. That setting already means "the size above which a single tool result is too
    /// large to keep inline", which is the same question asked here; two settings for one question is
    /// how the values drift apart and one of them silently stops matching what anyone believes.
    /// </para>
    /// <para>
    /// Deliberately <strong>not</strong> gated on <c>GovernanceConfig.Enabled</c>. Bounding is
    /// resource management, not a policy verdict — an unbounded tool result exhausts the context
    /// window whether or not a consumer has armed governance, exactly as the unconditional sanitize
    /// beside it applies regardless of any gate's verdict (#469).
    /// </para>
    /// </remarks>
    private int OutputCeiling =>
        _options.CurrentValue.AI.ContextManagement.ToolResultStorage.PerResultCharLimit;

    /// <inheritdoc />
    public object? ApplyOutputPolicy(ToolCallAdmission admission, string toolName, object? result)
    {
        ArgumentNullException.ThrowIfNull(admission);

        // #487: cut to a scan-cost-bounded region BEFORE sanitizing or redacting — the opposite order
        // from the final cut below, and safe only because of the overlap margin: see PreCutForScan's
        // remarks for the trade this accepts. Every caller of this method funnels through it, so this
        // is also what protects the agent-turn path (GovernedAIFunction), which used to have no bound
        // on scan cost at all.
        //
        // A real marker is passed, unlike TryApplyTextOutputPolicy's use of the same primitive: this
        // method has no out-parameter to report a drop through — see PreCutForScan's own marker doc,
        // and #487's security-review finding on this PR that this line closes.
        var (preCut, _) = ToolResultText.PreCutForScan(result, OutputCeiling, ScrubOverlapMargin, OutputTruncationMarker);

        // #469: the sanitize pass below is unconditional — see the interface remarks for why. It stays
        // in shape-preserving lockstep with RedactResult below via the shared ToolResultText.Sanitize.
        var treated = admission.RedactsOutput
            ? _classificationGate.RedactResult(toolName, preCut)
            : ToolResultText.Sanitize(preCut, _sanitizer, toolName);

        // #532: bound AFTER sanitize and redact, never before the FINAL cut — the pre-cut above is a
        // different, wider, scan-cost-only bound, not a substitute for this one. It also mirrors
        // ReportExecutionAsync, which has always sanitized, redacted, and THEN bounded tool failure
        // text at this same pipeline (#460); success output simply never got the third step.
        return ToolResultText.Bound(treated, OutputCeiling, OutputTruncationMarker);
    }

    /// <inheritdoc />
    public bool TryApplyTextOutputPolicy(
        ToolCallAdmission admission,
        string toolName,
        string? content,
        out string? result,
        out bool wasTruncated)
    {
        ArgumentNullException.ThrowIfNull(admission);

        // Every early return below leaves nothing to report; only the one branch that actually cuts
        // overwrites this. Set once here so no future branch can forget it and silently answer "not
        // truncated" — which is the exact failure this out-parameter exists to make impossible.
        wasTruncated = false;

        // #487: the same scan-cost pre-cut ApplyOutputPolicy applies, for the same reason — this is the
        // method the plan step executor (a sandboxed tool's output, unbounded upstream) and the
        // Execution API both call, and neither used to bound scan cost on this side of the boundary.
        var (preCut, droppedByPreCut) = ToolResultText.PreCutForScan(content, OutputCeiling, ScrubOverlapMargin);

        // #479: sanitize unconditionally on both branches, the same guarantee ApplyOutputPolicy carries
        // (#469) — this method used to sanitize only when a redaction was required, leaving the
        // invariant enforced by caller discipline (both current callers ran their own unconditional
        // scrub immediately after) rather than by this interface itself.
        //
        // Null content is deliberately NOT special-cased ahead of this branch — same reason as before
        // #490: on the non-redact branch, Sanitize's null-in/null-out guarantee (now structural, not
        // re-derived from Transform's switch on every read) means a null preCut reaches the null branch
        // below cleanly. On the REDACT branch it is deliberately NOT short-circuited the same way: a
        // redact-required call answering with null is treated identically whether preCut was null (the
        // well-behaved gate correctly echoing "nothing to redact") or the gate broke its contract on
        // real input — see the fail-closed branch below for why collapsing that distinction is the
        // point, not an oversight (#479's original regression).
        var processed = admission.RedactsOutput
            ? _classificationGate.RedactResult(toolName, preCut)
            : ToolResultText.Sanitize(preCut, _sanitizer, toolName);

        if (processed is null)
        {
            if (!admission.RedactsOutput)
            {
                // Non-redact branch: Sanitize's null-in/null-out guarantee (#490) means reaching here
                // can only mean preCut was null — nothing to sanitize.
                result = null;
                return true;
            }

            // Fail closed, deliberately without asking whether preCut was itself null. A redact-required
            // call that produced nothing to redact and a redact-required call whose gate broke the
            // non-null-in/non-null-out contract on real input are indistinguishable from outside the
            // gate, and #479's regression was exactly a refactor that treated "no content yet" as safe
            // to short-circuit past this branch — turning a denial into a reported success with no
            // fail-closed signal at all. Falling back to the original would be the harmless-looking
            // default that defeats the control; the shipped gate always honors the contract, so this
            // guards against a consumer-supplied one, same as it always has.
            _logger.LogWarning(
                "Classification gate returned null when redacting output of {ToolName}; the result is "
                + "withheld rather than returned unredacted.",
                toolName);
            result = null;
            return false;
        }

        // #532: bound after sanitize/redact — see ApplyOutputPolicy for why the order is fixed. This is
        // the plan path's only cut. It is NOT the Execution API's only cut, and an earlier version of
        // this comment claimed the API was "unaffected while its ceiling is no larger" — a
        // precondition that does not hold on shipped defaults (its 262,144 against this 50,000), so its
        // own before-and-after checks both saw an unchanged string while this step had already removed
        // most of the body. Rather than restore that assumption by aligning the two numbers, the cut
        // now reports itself and the caller ORs it into whatever truncation signal it publishes: a fact
        // that travels with the value cannot drift apart the way two independently-owned ceilings can.
        var (bounded, cutAfterProcessing) = BoundedText.Cap(processed, OutputCeiling, OutputTruncationMarker);
        result = bounded;
        // droppedByPreCut is carried forward, not discarded: content the pre-cut dropped can still
        // leave this final cut with nothing further to drop once sanitizing/redacting has shrunk what
        // remains — this cut's own flag alone would under-report what was actually lost (#487/#493,
        // the same reasoning DirectToolInvoker's now-retired ScrubAndBound used to carry by hand).
        wasTruncated = droppedByPreCut || cutAfterProcessing;
        return true;
    }

    /// <inheritdoc />
    public ValueTask ReportExecutionAsync(
        ToolCallAdmission admission, ToolExecutionReport report, string reportedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);

        // Not validated eagerly with a throw: this method's own contract is "never throws" (see
        // the interface doc), matching the must-not-throw guarantee of everything it delegates
        // to. A blank reportedBy from a broken caller is a caller bug — EscalationExecutionRecord's
        // factories still reject it, but inside DefaultApprovalExecutionReporter's own try/catch,
        // where it is logged rather than thrown, consistent with every other failure on this path.
        if (admission.ApprovedCall is not { } call)
            return ValueTask.CompletedTask;

        return report switch
        {
            { Status: EscalationExecutionStatus.Succeeded } =>
                _executionReporter.ReportSucceededAsync(call, reportedBy, cancellationToken),
            // #460: the tool's raw failure text is sanitized, redacted, and bounded exactly once, here
            // — the chokepoint every reporting path already funnels through — rather than by each
            // caller. See ReportedFailureText.PrepareForReporting for the ordering rationale.
            //
            // Prepared through SafePrepareFailureText, not called inline: an argument expression runs
            // before ReportFailedAsync's own body, so a throw here — a regex match timeout, or any
            // exception a consumer-supplied ICompositeResponseSanitizer/IContentRedactionFilter could
            // raise — would otherwise escape this method entirely, breaking the must-not-throw contract
            // both GovernedAIFunction and DirectToolInvoker rely on (see their own remarks) and losing
            // the audit write and approver notification with no compensating log.
            { Status: EscalationExecutionStatus.Failed, FailureReason: { } reason } =>
                _executionReporter.ReportFailedAsync(
                    call, SafePrepareFailureText(reason, report.ToolName), reportedBy, cancellationToken),
            { Status: EscalationExecutionStatus.NeverExecuted, NotExecutedReason: { } notExecuted } =>
                _executionReporter.ReportNotExecutedAsync(call, notExecuted, reportedBy, cancellationToken),
            // An incoherent report (Failed with no reason, NeverExecuted with no reason) is a
            // caller bug, not something to guess at or throw over — this is the one place in the
            // execution-reporting path with no must-not-throw contract protecting it yet.
            _ => ValueTask.CompletedTask
        };
    }

    /// <summary>
    /// Wraps <see cref="ReportedFailureText.PrepareForReporting"/> so a failure in sanitizing or
    /// redacting a tool's failure text degrades to a withheld-text placeholder instead of throwing —
    /// restoring this method's own must-not-throw contract, which an argument-position call would
    /// otherwise bypass entirely (the exception would propagate before <see cref="ReportExecutionAsync"/>
    /// ever reaches its own body). Fails closed: never returns the raw, untreated text on this path.
    /// </summary>
    private string SafePrepareFailureText(string reason, string? toolName)
    {
        try
        {
            return ReportedFailureText.PrepareForReporting(reason, _sanitizer, _redactionFilter, toolName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to sanitize/redact failure text for {ToolName}; withholding raw text from the report",
                toolName);
            return "[tool failure text withheld: sanitization or redaction failed]";
        }
    }

    /// <inheritdoc />
    public GovernanceTrace GetTrace() => _trace.Snapshot();

    /// <inheritdoc />
    public void Reset()
    {
        _trace.Reset();
        _progressEvaluator.Reset();
    }

    // Blank counts as absent, not just null: whitespace reaches a model as indistinguishable from an
    // empty result, which reads as the tool having run and returned nothing rather than as a refusal.
    private static ToolCallAdmission Refuse(string? stageMessage, string toolName) =>
        ToolCallAdmission.Deny(
            string.IsNullOrWhiteSpace(stageMessage) ? GovernanceDenials.NotPermitted(toolName) : stageMessage);

    /// <summary>
    /// Builds a stable, deterministic signature of the call arguments so the loop guard can recognise
    /// identical calls. Keys are ordered; each value is JSON-serialised, falling back to its type name
    /// if serialisation throws — the signature is always computable and never throws on the hot path.
    /// </summary>
    private static string? ComputeArgumentsSignature(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return string.Empty;

        var parts = new List<string>(arguments.Count);
        foreach (var kvp in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            string value;
            try
            {
                value = kvp.Value is null ? "null" : JsonSerializer.Serialize(kvp.Value);
            }
            catch
            {
                value = kvp.Value?.GetType().FullName ?? "null";
            }

            parts.Add(string.Concat(kvp.Key, "=", value));
        }

        return string.Join(ArgPairSeparator, parts);
    }
}

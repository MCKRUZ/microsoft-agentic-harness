using System.Collections.ObjectModel;
using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
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
    private readonly IAgentExecutionContext _executionContext;
    private readonly IToolResultStore _resultStore;

    // #522: running total of characters actually reserved toward this turn's aggregate output
    // budget (ToolResultStorageConfig.AggregatePerMessageCharLimit) — see ReserveAggregateCeiling's
    // remarks for why this needs an atomic reserve/settle pair rather than a plain read-then-add.
    private readonly Lock _aggregateBudgetLock = new();
    private int _aggregateCharsReserved;

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
    /// <see cref="ApplyOutputPolicyAsync"/> — see #469; that path never reaches
    /// <paramref name="redactionFilter"/> at all.
    /// </param>
    /// <param name="redactionFilter">Scrubs known secret patterns from a failed call's reported text.</param>
    /// <param name="logger">Records a redaction that could not be applied.</param>
    /// <param name="executionContext">
    /// Supplies <see cref="IAgentExecutionContext.ToolResultScopeId"/> — the isolation boundary a
    /// truncated result is spilled under (#521). Scoped in DI, same lifetime as this pipeline, so
    /// plain constructor injection is correct here; no ambient lookup needed.
    /// </param>
    /// <param name="resultStore">
    /// Where a truncated result's full text is spilled so a later <c>tool_result_fetch</c> call can
    /// retrieve it (#521) — unconditionally redacted by the store itself, before the write, regardless
    /// of this call's own admission (#563; see <see cref="SpillAndBuildMarkerAsync"/>'s own remarks).
    /// </param>
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
        ILogger<ToolCallAdmissionPipeline> logger,
        IAgentExecutionContext executionContext,
        IToolResultStore resultStore)
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
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(resultStore);

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
        _executionContext = executionContext;
        _resultStore = resultStore;
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
    /// Format string for the marker embedded when a truncated result was spilled and can be retrieved
    /// via <c>tool_result_fetch</c> (#521) — <c>{0}</c> is the spilled result's id. The single source
    /// of truth for this text: <c>ToolResultFetchTool.Description</c> (Infrastructure.AI, which may
    /// reference Application.AI.Common) formats the same string with a placeholder id rather than
    /// hand-copying the phrase, so the tool's own self-description can never silently drift from the
    /// marker text it actually needs to recognize (a reuse finding from this package's `/simplify` pass).
    /// </summary>
    /// <remarks>
    /// Says "in pages", not "full output" (#563): what is spilled can itself exceed
    /// <c>ToolResultStorageConfig.MaxSpillChars</c> and be capped there too, and even within that cap
    /// <c>tool_result_fetch</c> only ever returns one bounded page at a time — the same scan-cost bound
    /// this pipeline applies to every other tool result applies to a fetched page as well, since a page
    /// flows back through this same pipeline like any other tool output. "Full output" was true of
    /// neither case and is no longer promised.
    /// </remarks>
    public const string SpilledResultMarkerFormat =
        "\n[tool output truncated — retrieve the rest in pages with tool_result_fetch, id={0}]";

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

    /// <summary>
    /// Atomically claims up to <paramref name="perResultCeiling"/> characters from this turn's
    /// remaining <c>AggregatePerMessageCharLimit</c> budget (#522) and returns the ceiling this one
    /// call must cut to. Always call <see cref="SettleAggregateReservation"/> with the same value once
    /// the actual output length is known, or later calls in the same turn are starved by budget this
    /// call reserved but never used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a reserve/settle pair instead of reading "remaining" once and subtracting the
    /// actual length afterward.</strong> Parallel tool calls in one turn run concurrently — the same
    /// concurrency <see cref="ReplayedToolCallSet"/>'s remarks document for
    /// <c>FunctionInvokingChatClient</c>'s <c>AllowConcurrentInvocation = true</c>. Reading remaining
    /// budget, cutting, and only then subtracting the real length leaves the same race that type's own
    /// remarks warn against: two calls can both read the same "remaining" before either commits, both
    /// cut to that full amount, and the turn ends up having emitted up to twice the configured budget.
    /// Reserving <paramref name="perResultCeiling"/> up front — atomically, under the same lock that
    /// reads remaining — means the running total can never be over-committed, because every caller's
    /// share is subtracted before it does any cutting, not after.
    /// </para>
    /// <para>
    /// The reservation is deliberately the full per-result ceiling, not a guess at the eventual output
    /// size: at reservation time this call has not yet sanitized, redacted, or cut anything, so the
    /// ceiling is the only size it can name with certainty won't be exceeded.
    /// <see cref="SettleAggregateReservation"/> gives back whatever fraction of that reservation the
    /// call didn't actually use, so a handful of small results does not exhaust the aggregate budget
    /// the way a handful of large ones should.
    /// </para>
    /// <para>
    /// <strong>Never returns less than <see cref="OutputTruncationMarker"/>'s own length plus one
    /// (bounded by <paramref name="perResultCeiling"/>).</strong> Correctness review and security
    /// review both independently flagged the version of this method that returned exactly
    /// <c>remaining</c> even when it had reached zero: once a turn's aggregate budget was fully spent,
    /// every later result was cut to an empty string — <see cref="BoundedText.Cap"/>'s own contract
    /// drops a marker outright unless the ceiling it is given is <em>strictly greater than</em> the
    /// marker's length (<c>ceiling &gt; marker.Length</c>, not <c>&gt;=</c> — the marker needs at least
    /// one character of surviving content alongside it to be appended at all), so a zero (or
    /// near-zero) ceiling left no marker and no retrieval id, re-opening #487's "no silent caps"
    /// finding at the turn level instead of the per-result level it was originally closed at. This
    /// floor guarantees every truncated result carries at least the plain truncation signal — the
    /// id-carrying marker (#521, ~107 chars) may still be dropped by the existing
    /// <c>idMarkerLanded</c> fallback when the budget is this tight, exactly as it already is for a
    /// merely small per-result ceiling; only the PLAIN marker's visibility is guaranteed here. The
    /// floor lets the aggregate ledger run up to <see cref="OutputTruncationMarker"/>'s length plus one
    /// past the configured limit once budget is effectively exhausted (every subsequent call in the
    /// turn reserves the floor, not zero) — a small, bounded softening of a budget this package
    /// invented, not a violation of <paramref name="perResultCeiling"/> itself, which nothing here ever
    /// exceeds.
    /// </para>
    /// </remarks>
    private int ReserveAggregateCeiling(int perResultCeiling)
    {
        var aggregateLimit = _options.CurrentValue.AI.ContextManagement.ToolResultStorage.AggregatePerMessageCharLimit;
        var minimumViable = Math.Min(perResultCeiling, OutputTruncationMarker.Length + 1);

        lock (_aggregateBudgetLock)
        {
            var remaining = Math.Max(0, aggregateLimit - _aggregateCharsReserved);
            var reserved = Math.Max(minimumViable, Math.Min(perResultCeiling, remaining));
            _aggregateCharsReserved += reserved;
            return reserved;
        }
    }

    /// <summary>
    /// Returns whatever part of <paramref name="reserved"/> this call did not actually spend, once the
    /// final output length is known — see <see cref="ReserveAggregateCeiling"/>. A no-op when the call
    /// used everything it reserved (the common case once the aggregate budget is genuinely tight).
    /// </summary>
    /// <param name="reserved">The value <see cref="ReserveAggregateCeiling"/> returned for this call.</param>
    /// <param name="actualLength">
    /// The final emitted text's length. Never exceeds <paramref name="reserved"/> — this call's own
    /// cut was bounded to it — so this only ever gives budget back, never claims more.
    /// </param>
    private void SettleAggregateReservation(int reserved, int actualLength)
    {
        var unused = reserved - actualLength;
        if (unused <= 0)
            return;

        lock (_aggregateBudgetLock)
        {
            _aggregateCharsReserved -= unused;
        }
    }

    /// <inheritdoc />
    public async ValueTask<object?> ApplyOutputPolicyAsync(
        ToolCallAdmission admission, string toolName, object? result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();

        // #487: cut to a scan-cost-bounded region BEFORE sanitizing or redacting — the opposite order
        // from the final cut below, and safe only because of the overlap margin: see PreCutForScan's
        // remarks for the trade this accepts. Every caller of this method funnels through it, so this
        // is also what protects the agent-turn path (GovernedAIFunction), which used to have no bound
        // on scan cost at all.
        //
        // A real marker is passed, unlike TryApplyTextOutputPolicyAsync's use of the same primitive:
        // this method has no truncation signal from the pre-cut alone — see PreCutForScan's own marker
        // doc, and #487's security-review finding on this PR that this line closes. #521's spill below
        // therefore only fires on the FINAL cut's own drop, not the pre-cut's — a pre-cut-only drop
        // still leaves its own embedded marker (unchanged, pre-existing behavior) but is not spilled;
        // narrower than TryApplyTextOutputPolicyAsync's coverage, and stated here rather than left
        // implicit, matching this repo's own "no silent caps" convention.
        //
        // Ceiling captured once, not re-read from _options.CurrentValue at the pre-cut and again at the
        // final cut: a hot reload between the two reads would otherwise let them disagree about what
        // ceiling this one call is bounding to (run-gates' correctness gate, advisory).
        var ceiling = OutputCeiling;
        var (preCut, _) = ToolResultText.PreCutForScan(result, ceiling, ScrubOverlapMargin, OutputTruncationMarker);

        // #469: the sanitize pass below is unconditional — see the interface remarks for why. It stays
        // in shape-preserving lockstep with RedactResult below via the shared ToolResultText.Sanitize.
        // Delegated to SanitizeOrRedact (see its own remarks) so a sanitizer/redaction-gate exception
        // degrades gracefully instead of faulting the whole tool call.
        var treated = SanitizeOrRedact(admission, toolName, preCut);

        // #532: bound AFTER sanitize and redact, never before the FINAL cut — the pre-cut above is a
        // different, wider, scan-cost-only bound, not a substitute for this one. It also mirrors
        // ReportExecutionAsync, which has always sanitized, redacted, and THEN bounded tool failure
        // text at this same pipeline (#460); success output simply never got the third step.
        //
        // #522: the FINAL cut alone respects the aggregate per-message budget, via effectiveCeiling —
        // never the pre-cut above, which stays on the full per-result ceiling because it bounds SCAN
        // cost, not output size (see ReserveAggregateCeiling's remarks for why a smaller pre-cut would
        // still be safe but is not necessary here).
        var effectiveCeiling = ReserveAggregateCeiling(ceiling);
        var (bounded, dropped) = ToolResultText.Bound(treated, effectiveCeiling, OutputTruncationMarker);
        if (!dropped)
        {
            SettleAggregateReservation(effectiveCeiling, ToolResultText.ExtractText(bounded).Length);
            return bounded;
        }

        // #577: `dropped` above reflects TREATED (sanitized/redacted) length crossing effectiveCeiling —
        // but sanitizing and redacting only ever grow text (a secret becomes a longer placeholder), and
        // FileSystemToolResultStore.StoreIfLargeAsync decides inline-vs-spill from RAW length against
        // this same ceiling. A result whose raw length was already at or under the ceiling can still
        // trip `dropped` here purely from that inflation, even though the store would have kept the
        // very same content inline with no spill needed — spilling it anyway and promising a retrieval
        // id is dishonest about why the call needed one at all.
        //
        // Correctness-review finding on the first cut of this fix: it returned `treated` UNCUT, which
        // can genuinely exceed effectiveCeiling (that is exactly why `dropped` fired) and broke
        // SettleAggregateReservation's own documented contract that actualLength never exceeds what was
        // reserved — passing a larger actualLength doesn't overcharge the tracker, it silently UNDER-
        // charges it (`unused = reserved - actualLength` goes negative, and the `unused <= 0` guard
        // no-ops instead of debiting the overage), letting a later call in the same turn see more of
        // AggregatePerMessageCharLimit than it should. The per-message ceiling is a hard resource bound
        // and #577 was never about relaxing it — only about not promising a fetch nothing will improve.
        // `bounded` (computed above, already correctly capped to effectiveCeiling with the plain,
        // non-retrieval marker) is the right text either way: same answer as the "nothing was truncated"
        // branch above, just reached from a different guard.
        //
        // Also only worth checking when a real spill could actually happen: when the scope isn't
        // retrievable, SpillAndBuildMarkerAsync (below) already degrades to the plain
        // OutputTruncationMarker with no disk write — identical to `bounded` — so there is no false
        // promise to correct on that path, and evaluating ExtractText(result) here would be pure waste
        // on exactly the path its own factory-laziness (see that method's remarks) exists to avoid
        // paying for.
        var rawFullTextCache = (string?)null;
        string RawFullText() => rawFullTextCache ??= ToolResultText.ExtractText(result);

        if (_executionContext.HasRetrievableToolResultScope && RawFullText().Length <= effectiveCeiling)
        {
            SettleAggregateReservation(effectiveCeiling, ToolResultText.ExtractText(bounded).Length);
            return bounded;
        }

        // #563: the ORIGINAL result — before the scan-cost pre-cut, before sanitize/redact — is what
        // gets spilled, not `treated`. Spilling treated (as before #563) meant the stored copy was
        // already cut to the scan-cost bound (ceiling + ScrubOverlapMargin), so tool_result_fetch could
        // never actually return more than that regardless of how large the tool's real output was — the
        // "full output available" marker overpromised for anything past roughly 58KB at shipped
        // defaults. The re-cut below still operates on `treated`, unchanged: the MODEL-facing text for
        // THIS call is exactly as it always was, only the spilled copy's source changed.
        // Security-review finding, now on its third revision: the STORE redacts the spilled copy
        // unconditionally — not gated on admission.RedactsOutput. Two earlier revisions each regressed
        // a guarantee the other one had: redacting each fetched PAGE at read time (broken, because a
        // page boundary is a character offset the model chooses freely via tool_result_fetch's own
        // 'offset' argument, so a secret could be split across two boundaries and recovered unredacted
        // from both halves); then redacting at write time but only when THIS call's own admission
        // required it (broken, because a plain-allow call — the common case — spilled raw, unscanned
        // content, regressing the unconditional at-rest redaction this store always did before #563).
        // See StoreIfLargeAsync's own remarks for the full history and why unconditional is what
        // finally closes both bypasses at once.
        //
        // RawFullText, not a fresh closure, so a scope-retrievable call that already computed it above
        // for the #577 check does not walk-and-rejoin every block of `result` a second time.
        var marker = await SpillAndBuildMarkerAsync(toolName, RawFullText, effectiveCeiling)
            .ConfigureAwait(false);
        var (reboundedWithId, _) = ToolResultText.Bound(treated, effectiveCeiling, marker);

        // A code-review found this residual gap: ToolResultText.Bound/BudgetedCut walks a multi-block
        // result with a single shared per-block budget, and cuts whichever block first exceeds it.
        // BoundedText.Cap's own documented contract silently DROPS a marker (not overshoots) whenever
        // that block's local remaining budget is smaller than the marker's own length — every block
        // after the cut then runs with a zero budget and is silently emptied too, also markerless. The
        // short OutputTruncationMarker (~25 chars) made this window narrow; the id-carrying marker
        // (~107 chars, carrying a GUID) widens it roughly 4x, and #539's own multi-block test
        // (ApplyOutputPolicy_MultipleTextBlocks_BoundsTheTOTALNotEachBlock) proves multi-block results
        // are a first-class shape this method handles, not a contrived one. Rather than reworking
        // BudgetedCut's per-block budget algorithm to guarantee room wherever a cut could land, fall
        // back to the already-correct, already-computed `bounded` (plain marker) whenever the id
        // marker didn't actually survive the re-cut — the model always gets an honest truncation
        // signal, even on the rarer path where this call can't fit a retrieval id in the space left.
        //
        // reboundedText is extracted once and reused for both the Contains check and the settle
        // length below — ExtractText walks and rejoins every text block, so re-running it on the same
        // object a second time would double that cost on every truncated multi-block result.
        var reboundedText = ToolResultText.ExtractText(reboundedWithId);
        var idMarkerLanded = reboundedText.Contains(marker, StringComparison.Ordinal);
        var final = idMarkerLanded ? reboundedWithId : bounded;
        var finalLength = idMarkerLanded ? reboundedText.Length : ToolResultText.ExtractText(bounded).Length;
        SettleAggregateReservation(effectiveCeiling, finalLength);
        return final;
    }

    /// <inheritdoc />
    public async ValueTask<TextOutputPolicyResult> TryApplyTextOutputPolicyAsync(
        ToolCallAdmission admission,
        string toolName,
        string? content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();

        // #487: the same scan-cost pre-cut ApplyOutputPolicyAsync applies, for the same reason — this is
        // the method the plan step executor (a sandboxed tool's output, unbounded upstream) and the
        // Execution API both call, and neither used to bound scan cost on this side of the boundary.
        //
        // An empty marker is passed here, unlike ApplyOutputPolicyAsync's use of the same primitive:
        // this method reports a drop through the returned WasTruncated instead — droppedByPreCut is
        // combined with the final cut's own flag below rather than embedding a marker twice.
        //
        // Ceiling captured once, not re-read at the pre-cut and again at the final cut below — see
        // ApplyOutputPolicyAsync's identical capture for why (run-gates' correctness gate, advisory).
        var ceiling = OutputCeiling;
        var (preCut, droppedByPreCut) =
            ToolResultText.PreCutForScan(content, ceiling, ScrubOverlapMargin, OutputTruncationMarker);

        // #479: sanitize unconditionally on both branches, the same guarantee ApplyOutputPolicyAsync
        // carries (#469) — this method used to sanitize only when a redaction was required, leaving the
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
        // point, not an oversight (#479's original regression). Delegated to SanitizeOrRedact (see its
        // own remarks) so a sanitizer/redaction-gate exception reaches the SAME null branch below as a
        // gate that broke its contract by returning null, rather than faulting the whole tool call.
        var processed = SanitizeOrRedact(admission, toolName, preCut);

        if (processed is null)
        {
            if (!admission.RedactsOutput)
            {
                // Non-redact branch: Sanitize's null-in/null-out guarantee (#490) means reaching here
                // can only mean preCut was null — nothing to sanitize.
                return new TextOutputPolicyResult(Success: true, Result: null, WasTruncated: false);
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
            return new TextOutputPolicyResult(Success: false, Result: null, WasTruncated: false);
        }

        // #532: bound after sanitize/redact — see ApplyOutputPolicyAsync for why the order is fixed.
        // This is the plan path's only cut. It is NOT the Execution API's only cut, and an earlier
        // version of this comment claimed the API was "unaffected while its ceiling is no larger" — a
        // precondition that does not hold on shipped defaults (its 262,144 against this 50,000), so its
        // own before-and-after checks both saw an unchanged string while this step had already removed
        // most of the body. Rather than restore that assumption by aligning the two numbers, the cut
        // now reports itself and the caller ORs it into whatever truncation signal it publishes: a fact
        // that travels with the value cannot drift apart the way two independently-owned ceilings can.
        // #522: the final cut alone respects the aggregate per-message budget, via effectiveCeiling —
        // see ApplyOutputPolicyAsync's identical comment for why the pre-cut above stays on the full
        // per-result ceiling.
        var effectiveCeiling = ReserveAggregateCeiling(ceiling);
        var (bounded, cutAfterProcessing) = BoundedText.Cap(processed, effectiveCeiling, OutputTruncationMarker);
        // droppedByPreCut is carried forward, not discarded: content the pre-cut dropped can still
        // leave this final cut with nothing further to drop once sanitizing/redacting has shrunk what
        // remains — this cut's own flag alone would under-report what was actually lost (#487/#493,
        // the same reasoning DirectToolInvoker's now-retired ScrubAndBound used to carry by hand).
        var wasTruncated = droppedByPreCut || cutAfterProcessing;

        // #577: same fix as ApplyOutputPolicyAsync's identical comment — cutAfterProcessing alone can
        // fire purely from sanitize/redact inflation even when the untreated `content` already fit
        // under effectiveCeiling, disagreeing with FileSystemToolResultStore.StoreIfLargeAsync's own
        // raw-length-based inline decision. Spilling and promising a retrieval id in that case is
        // dishonest about why the call needed one at all.
        //
        // Correctness-review finding on the first cut of this fix (same defect as ApplyOutputPolicyAsync's
        // identical corrected comment): it returned `processed` UNCUT, which can genuinely exceed
        // effectiveCeiling and silently under-charges SettleAggregateReservation's aggregate ledger
        // (`unused = reserved - actualLength` goes negative and the `unused <= 0` guard no-ops rather
        // than debiting the overage). `bounded` — already correctly capped, computed above — is the
        // right text either way.
        //
        // The `!droppedByPreCut` conjunct that used to gate this is provably redundant, not merely
        // simplifiable: effectiveCeiling <= ceiling (ReserveAggregateCeiling's Math.Max/Math.Min both
        // cap at perResultCeiling) < ceiling + ScrubOverlapMargin (the pre-cut's own threshold), so
        // content.Length <= effectiveCeiling already implies content.Length is under the pre-cut's
        // threshold too — droppedByPreCut cannot be true when this branch's own length check passes.
        // Also only checked when a real spill could actually happen: when the scope isn't retrievable,
        // SpillAndBuildMarkerAsync (below) already degrades to the plain marker with no disk write —
        // identical to `bounded` — so there is no false promise to correct on that path.
        var rawFitsWithNoGenuinePromiseToMake =
            cutAfterProcessing && content is not null && content.Length <= effectiveCeiling
            && _executionContext.HasRetrievableToolResultScope;

        // Correctness-review finding on the SECOND cut of this fix: folding this case into the SAME
        // branch as `!wasTruncated` below and reporting WasTruncated: false was itself wrong — `bounded`
        // genuinely IS shorter than `processed` here (cutAfterProcessing is true by construction of
        // this branch's own guard). WasTruncated's contract (see its own XML doc on
        // TextOutputPolicyResult) is "did the pipeline cut the text to fit its ceiling", full stop — it
        // says nothing about whether a retrieval id was offered, and DirectToolInvoker publishes it as
        // an OutputTruncated signal independent of any marker text embedded in Result. Only the
        // RETRIEVAL PROMISE is what #577 has any business suppressing here, not the truncation fact
        // itself — caught because CI's correctness-review gate re-derives its own verdict from the
        // code, not from what a comment claims.
        if (rawFitsWithNoGenuinePromiseToMake)
        {
            SettleAggregateReservation(effectiveCeiling, bounded.Length);
            return new TextOutputPolicyResult(Success: true, Result: bounded, WasTruncated: true);
        }

        if (!wasTruncated)
        {
            SettleAggregateReservation(effectiveCeiling, bounded.Length);
            return new TextOutputPolicyResult(Success: true, Result: bounded, WasTruncated: false);
        }

        // #563: the ORIGINAL content — before the scan-cost pre-cut, before sanitize/redact — is what
        // gets spilled, not `processed`. See ApplyOutputPolicyAsync's identical comment for why: the
        // stored copy used to be cut to the scan-cost bound before it ever reached disk, so a fetched
        // page could never exceed that bound regardless of the tool's true output size. `content` is
        // non-null on every path a well-behaved classification gate can produce — see the null branch
        // above — and SpillAndBuildMarkerAsync's own catch-all degrades to the plain marker rather than
        // throwing on the one path a gate that violates its own null-in/null-out contract could still
        // reach this line with content actually null.
        //
        // The re-cut below still operates on `processed`, unchanged: the MODEL-facing text for THIS
        // call is exactly as it always was. The id-carrying marker is longer than OutputTruncationMarker,
        // so neither a post-hoc replace nor an unconditional append is safe here: cutAfterProcessing==true
        // means processed is already known to exceed ceiling, but droppedByPreCut-only means processed is
        // UNDER ceiling — Cap's normal "nothing to cut, marker not embedded" default would silently drop
        // the id, since this branch (unlike ApplyOutputPolicyAsync's) already knows wasTruncated=true
        // from a signal Cap itself never sees. alwaysEmbedMarker covers both: it only cuts as much of
        // processed as is needed to make room for the marker, never more, and never lets the result
        // exceed ceiling.
        // Security-review finding: same as ApplyOutputPolicyAsync's identical comment — the store
        // redacts the spill unconditionally, not gated on this call's own admission.
        var marker = await SpillAndBuildMarkerAsync(
            toolName, () => content!, effectiveCeiling).ConfigureAwait(false);
        var (withId, _) = BoundedText.Cap(processed, effectiveCeiling, marker, alwaysEmbedMarker: true);

        // #522: correctness-review and security-review finding on the PR that introduced the aggregate
        // budget's floor — ReserveAggregateCeiling's floor guarantees room for OutputTruncationMarker
        // (24 chars) but NOT for this id-carrying marker (~107 chars), so effectiveCeiling can still be
        // too small for `marker` to survive Cap's own "ceiling not larger than marker, drop it rather
        // than overshoot" contract. Unlike ApplyOutputPolicyAsync, this method had no fallback for that
        // case — the result silently lost every marker at once, not just the retrieval id, the same
        // "no signal at all" defect the floor was meant to close, just one marker size further out.
        // Falling back to a plain-marker cut mirrors ApplyOutputPolicyAsync's idMarkerLanded check, and
        // is guaranteed to land: effectiveCeiling is always > OutputTruncationMarker.Length by
        // construction of the floor, so alwaysEmbedMarker's append-and-cut branch always has room.
        if (!withId.Contains(marker, StringComparison.Ordinal))
        {
            var (plainBounded, _) = BoundedText.Cap(processed, effectiveCeiling, OutputTruncationMarker, alwaysEmbedMarker: true);
            SettleAggregateReservation(effectiveCeiling, plainBounded.Length);
            return new TextOutputPolicyResult(Success: true, Result: plainBounded, WasTruncated: true);
        }

        SettleAggregateReservation(effectiveCeiling, withId.Length);
        return new TextOutputPolicyResult(Success: true, Result: withId, WasTruncated: true);
    }

    /// <summary>
    /// Spills <paramref name="rawFullTextFactory"/> — the tool's original, untreated output, not yet
    /// sanitized, cut, or (model-facing) redacted — to <see cref="_resultStore"/> under this
    /// execution's <see cref="IAgentExecutionContext.ToolResultScopeId"/>, and returns the marker to
    /// substitute for the plain <see cref="OutputTruncationMarker"/>, carrying the resulting id so a
    /// later <c>tool_result_fetch</c> call can retrieve the rest, one page at a time (#521, #563).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Capped only by MaxSpillChars, not by the scan-cost bound (#563).</strong> Before #563
    /// this spilled <paramref name="rawFullTextFactory"/> already cut to
    /// <see cref="ToolResultText.PreCutForScan(object?, int, int, string)"/>'s scan-cost bound, so it
    /// could never actually hold more than roughly <see cref="OutputCeiling"/> +
    /// <see cref="ScrubOverlapMargin"/> characters: the "full output available via tool_result_fetch"
    /// marker overpromised for anything past that, permanently. Spilling the original instead — capped
    /// only by
    /// <see cref="Domain.Common.Config.AI.ContextManagement.ToolResultStorageConfig.MaxSpillChars"/> in
    /// <see cref="_resultStore"/>, a much larger bound meant for disk rather than a single scan pass —
    /// makes a genuinely larger retrievable range possible.
    /// </para>
    /// <para>
    /// <strong>The store redacts what it writes, unconditionally — not this method, and never on
    /// read.</strong> Two earlier revisions of this design each regressed a different guarantee: one
    /// redacted each fetched PAGE independently at read time, gated by a flag carried alongside the
    /// content — broken, because a page boundary is a character offset the model chooses freely via
    /// <c>tool_result_fetch</c>'s own <c>offset</c> argument, so a secret split across two page
    /// boundaries came back unredacted from both halves, and no per-page fix closes that. The next
    /// gated write-time redaction on THIS call's own <c>admission.RedactsOutput</c> — also broken,
    /// because a plain-allow call (the common case) spilled raw, unscanned content, regressing the
    /// unconditional at-rest redaction this store always did before #563 existed at all. Neither this
    /// method nor its caller passes a redaction decision to
    /// <c>IToolResultStore.StoreIfLargeAsync</c> any more — the store redacts every large result's
    /// content with every category, always, before the write, closing both bypasses by construction:
    /// no gate for an adversarial classification to sit outside of, and no page boundary for an
    /// adversarial offset to split across.
    /// </para>
    /// <para>
    /// That write-time redaction is an acceptable scan cost only because of three controls landing
    /// together:
    /// <see cref="Domain.Common.Config.AI.ContextManagement.ToolResultStorageConfig.MaxSpillChars"/>
    /// bounds how much text a single spill (and therefore a single redaction pass) can ever cover, the
    /// storage directory gets owner-only permissions, and a retention sweep prunes what accumulates
    /// there (#559, #527). A fetched page is still sanitized and bounded when it is READ — it flows
    /// back through this same pipeline like any other tool result — sanitize is a different,
    /// unconditional pass this method does not touch.
    /// </para>
    /// <para>
    /// A store failure degrades to the plain marker rather than throwing out of a cut every caller of
    /// this pipeline depends on completing — the same must-not-throw discipline this file applies
    /// everywhere else. Always writes with <see cref="CancellationToken.None"/>, never the caller's own
    /// token: the write finalizes output a tool call already produced, the same "already happened,
    /// don't abandon it" reasoning <see cref="ReportExecutionAsync"/> applies to reporting.
    /// </para>
    /// <para>
    /// Always passes <paramref name="sizeThreshold"/> through to <see cref="_resultStore"/> as its own
    /// <c>sizeThreshold</c> (#522), rather than leaving the store to compare against its
    /// config-derived <c>PerResultCharLimit</c>. This method is only ever reached when a caller's own
    /// cut already dropped content against that exact threshold — which, since the aggregate
    /// per-message budget can shrink the ceiling a single result is cut to well below
    /// <c>PerResultCharLimit</c>, no longer implies <paramref name="rawFullTextFactory"/> itself exceeds
    /// the store's own configured limit. Comparing against the caller's real threshold instead of the
    /// store's keeps this a genuine size check rather than an unconditional bypass: a normal-sized
    /// result cut only by the aggregate budget still gets persisted (fixing the gap where the store
    /// judged it too small and returned the plain marker for content that was, in fact, fully
    /// available and recoverable), while nothing here can force disk I/O for content the caller never
    /// actually decided needed spilling. A store failure degrades to the plain marker below exactly as
    /// before.
    /// </para>
    /// </remarks>
    private async ValueTask<string> SpillAndBuildMarkerAsync(
        string toolName, Func<string> rawFullTextFactory, int sizeThreshold)
    {
        // #559: a direct tool invocation mints a fresh, call-scoped ToolResultScopeId that dies with
        // the call — nothing durable can ever ask for it again, so a file written here would be
        // unreachable the instant this method returns. Skip the write itself, not just the id: the
        // prior behavior still persisted an orphaned file to disk on every truncation on this path,
        // forever, with no sweep to reclaim it and zero retrieval benefit.
        if (!_executionContext.HasRetrievableToolResultScope)
            return OutputTruncationMarker;

        try
        {
            // /simplify finding: rawFullText is supplied as a factory, not a plain string, so a caller
            // whose text requires real work to produce (ApplyOutputPolicyAsync's
            // ToolResultText.ExtractText walks and rejoins every block of a potentially multi-block,
            // unbounded — post-#563 — result) never pays that cost on the direct-invoke path the guard
            // above already rejects. Invoked INSIDE the try, not before it — /code-review finding: this
            // method's whole contract is "never throws", and ExtractText's own circular-reference
            // fallback (a JsonSerializer.Serialize call with no cycle handling) can throw; evaluating
            // the factory before the try would let that escape uncaught instead of degrading to the
            // plain marker like every other failure on this path.
            var rawFullText = rawFullTextFactory();

            var reference = await _resultStore
                .StoreIfLargeAsync(
                    _executionContext.ToolResultScopeId, toolName, operation: null, rawFullText,
                    scopeIsRetrievable: _executionContext.HasRetrievableToolResultScope,
                    sizeThreshold: sizeThreshold, CancellationToken.None)
                .ConfigureAwait(false);

            if (reference.FullContentPath is null)
                return OutputTruncationMarker;

            return string.Format(SpilledResultMarkerFormat, reference.ResultId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to spill truncated output of {ToolName} for later retrieval via tool_result_fetch; "
                + "the result stays truncated with no retrieval id",
                toolName);
            return OutputTruncationMarker;
        }
    }

    /// <summary>
    /// Runs the sanitize-or-redact step both output-policy methods share, degrading rather than
    /// faulting the turn if <see cref="ICompositeResponseSanitizer"/> or <see cref="IContentRedactionFilter"/>
    /// throws. <see cref="ToolResultText.Sanitize(object?, ICompositeResponseSanitizer, string)"/> and
    /// <see cref="IToolClassificationGate.RedactResult(string, object?)"/> are both must-not-throw
    /// contracts against the SHIPPED <see cref="ICompositeResponseSanitizer"/> implementation, which
    /// now fails each of its own rules open on a regex timeout rather than letting one propagate — so
    /// this guards specifically against a consumer-replaced sanitizer or redaction filter, both of
    /// which this template's DI surface allows swapping out.
    /// </summary>
    /// <remarks>
    /// The two branches degrade differently, matching the two guarantees this pipeline already makes
    /// (see this type's interface remarks). The baseline sanitize (<paramref name="admission"/>'s
    /// <c>RedactsOutput</c> false) is defense-in-depth against injection payloads, not a promise about
    /// secrets — degrading to <paramref name="preCut"/> unsanitized-by-this-pass costs the model a
    /// scrub pass, nothing more, the same trade a regex timeout inside the composite already makes for
    /// one rule. A required redaction (<c>RedactsOutput</c> true) exists specifically because the
    /// classification gate flagged this call's output as needing scrubbing before anything downstream
    /// sees it; degrading THAT branch to unredacted raw text would silently emit exactly the content
    /// redaction exists to withhold. Returning <see langword="null"/> instead reuses this method's own
    /// existing fail-closed handling for a redaction gate that returns null — <see cref="TryApplyTextOutputPolicyAsync"/>'s
    /// null branch below already withholds the result rather than emitting it, and this call site now
    /// reaches that same branch by the same route, whether the gate returned null or threw.
    /// </remarks>
    private object? SanitizeOrRedact(ToolCallAdmission admission, string toolName, object? preCut)
    {
        try
        {
            return admission.RedactsOutput
                ? _classificationGate.RedactResult(toolName, preCut)
                : ToolResultText.Sanitize(preCut, _sanitizer, toolName);
        }
        catch (Exception ex)
        {
            LogSanitizeOrRedactFailure(ex, toolName, admission.RedactsOutput);
            return admission.RedactsOutput ? null : preCut;
        }
    }

    /// <summary>
    /// String-typed overload for <see cref="TryApplyTextOutputPolicyAsync"/> — see the object
    /// overload's remarks.
    /// </summary>
    /// <remarks>
    /// <strong>Deliberately NOT a cast-through-the-object-overload delegation</strong>, unlike
    /// <see cref="ToolResultText.Sanitize(string?, ICompositeResponseSanitizer, string)"/>'s equivalent
    /// split — a /simplify suggestion to match that shape was tried and reverted (broke 4 real tests,
    /// not just their mocks). The difference: <c>ToolResultText.Sanitize</c>'s two overloads both
    /// funnel into the SAME <see cref="ICompositeResponseSanitizer.Sanitize"/> method, so casting
    /// through the object overload calls identical code either way. <see cref="IToolClassificationGate"/>'s <c>RedactResult</c>
    /// is two SEPARATE interface members —
    /// <see cref="IToolClassificationGate.RedactResult(string, object?)"/> and
    /// <see cref="IToolClassificationGate.RedactResult(string, string?)"/> — which a consumer-supplied
    /// implementation is free to give different behavior (the shipped one happens not to, but the
    /// interface makes no such promise). Delegating through the object overload silently calls the
    /// OTHER member than the one
    /// this method's own signature promises, changing which interface method runs for every consumer
    /// implementation and, concretely, for every test that mocks <c>RedactResult(string, string?)</c>
    /// specifically — overload resolution is a compile-time, static-type decision, not something a
    /// runtime cast can redirect back.
    /// </remarks>
    private string? SanitizeOrRedact(ToolCallAdmission admission, string toolName, string? preCut)
    {
        try
        {
            return admission.RedactsOutput
                ? _classificationGate.RedactResult(toolName, preCut)
                : ToolResultText.Sanitize(preCut, _sanitizer, toolName);
        }
        catch (Exception ex)
        {
            LogSanitizeOrRedactFailure(ex, toolName, admission.RedactsOutput);
            return admission.RedactsOutput ? null : preCut;
        }
    }

    private void LogSanitizeOrRedactFailure(Exception ex, string toolName, bool wasRedactRequired) =>
        _logger.LogWarning(ex,
            "Sanitizer/redaction gate threw while processing output of {ToolName}; {Outcome}",
            toolName,
            wasRedactRequired
                ? "the result is withheld rather than returned unredacted"
                : "the result is passed through unsanitized by this pass");

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
    private PreparedFailureText SafePrepareFailureText(string reason, string? toolName)
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
            // #472: named FailureTextSubstitution.TreatmentFailed rather than an orphan literal — the
            // third of three substitution reasons ReportedFailureText.PrepareForReporting's own two
            // (Oversized, SanitizedToEmpty) previously left this one uncovered by the same discriminator.
            return new PreparedFailureText(
                "[tool failure text withheld: sanitization or redaction failed]",
                FailureTextSubstitution.TreatmentFailed);
        }
    }

    /// <inheritdoc />
    public GovernanceTrace GetTrace() => _trace.Snapshot();

    /// <inheritdoc />
    public void Reset()
    {
        _trace.Reset();
        _progressEvaluator.Reset();

        // #522: every Reset() call site (agent turn, orchestrated task, eval run, direct-invoke
        // arming) marks the start of a new unit of work, which is exactly the boundary the aggregate
        // output budget should restart at — see ReserveAggregateCeiling's remarks.
        lock (_aggregateBudgetLock)
        {
            _aggregateCharsReserved = 0;
        }
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

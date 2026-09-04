using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Domain.Common.Helpers;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolApprovalRouter"/>: raises a blocking <see cref="IEscalationService"/>
/// request for a tool call the governor will not auto-approve, and translates the human decision
/// back into an allow or a block.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in and doubly gated — both <c>GovernanceConfig.ToolApproval.Enabled</c> and
/// <c>GovernanceConfig.Escalation.Enabled</c> must be true. Off either, this returns
/// <see cref="ToolApprovalOutcome.NotRouted"/> and the governor blocks exactly as it did before.
/// </para>
/// <para>
/// <strong>Fail-closed at every exit.</strong> A missing roster, a denial, a timeout, a cancelled
/// turn, or an exception from the escalation service all resolve to a block. The only path to
/// <see cref="ToolApprovalOutcome.Approved"/> is an escalation that resolved with
/// <c>IsApproved</c> true.
/// </para>
/// </remarks>
public sealed class EscalationToolApprovalRouter : IToolApprovalRouter
{
    // An approver reading a tool call needs to recognise the action, not audit a payload. Long
    // values are truncated so a multi-megabyte argument cannot bloat the notification, the durable
    // escalation record, or the JSONL audit line.
    private const int MaxArgumentValueLength = 512;
    private const int MaxArgumentCount = 32;

    private readonly IEscalationService _escalationService;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly IAgentExecutionContext _executionContext;
    private readonly IApprovalFailureMemory _failureMemory;
    private readonly ILogger<EscalationToolApprovalRouter> _logger;

    // A misconfigured roster is a standing condition, not a per-call event. Warning once keeps the
    // signal without emitting a line on every approval-required call for the life of the process.
    //
    // Static because the router is registered scoped: an instance field resets on every turn, which
    // is "once per turn", not "once". Interlocked rather than a plain bool so concurrent turns cannot
    // both observe false and both warn.
    private static int s_blankApproversWarned;
    private static int s_escalationDisabledWarned;

    /// <summary>Initializes a new instance of the <see cref="EscalationToolApprovalRouter"/> class.</summary>
    public EscalationToolApprovalRouter(
        IEscalationService escalationService,
        ICompositeResponseSanitizer sanitizer,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        IAgentExecutionContext executionContext,
        IApprovalFailureMemory failureMemory,
        ILogger<EscalationToolApprovalRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(escalationService);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(governanceConfig);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(failureMemory);
        ArgumentNullException.ThrowIfNull(logger);

        _escalationService = escalationService;
        _sanitizer = sanitizer;
        _governanceConfig = governanceConfig;
        _executionContext = executionContext;
        _failureMemory = failureMemory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ToolApprovalResult> RequestApprovalAsync(
        string agentId,
        string toolName,
        string reason,
        BlastRadius radius,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var governance = _governanceConfig.CurrentValue;
        var approval = governance.ToolApproval;

        if (!approval.Enabled)
            return ToolApprovalResult.NotRouted("tool approval routing is disabled");

        // The escalation subsystem's own master switch. Routing to a subsystem the operator has
        // turned off would raise requests nothing delivers or resolves.
        //
        // Reaching here means the operator explicitly enabled approval routing while leaving the
        // subsystem it depends on switched off, so every approval-required call is refused forever.
        // That is a configuration mistake, not a deployment posture, and it was previously the only
        // exit from this method that said nothing at all — the operator saw tool calls being refused
        // with no indication that the gate they turned on was never running.
        if (!governance.Escalation.Enabled)
        {
            if (Interlocked.Exchange(ref s_escalationDisabledWarned, 1) == 0)
                _logger.LogWarning(
                    "AppConfig:AI:Governance:ToolApproval:Enabled is true but Escalation:Enabled is false — " +
                    "no approval can ever be requested, so every approval-required tool call is refused. " +
                    "Enable the escalation subsystem or turn tool approval routing off.");

            return ToolApprovalResult.NotRouted("escalation subsystem is disabled");
        }

        // An escalation with nobody on the roster can never be answered — it would stall the turn
        // until it timed out and then block anyway. Refuse immediately instead, and say why.
        //
        // Blank entries are dropped rather than passed through: EscalationRequestInvariants rejects
        // an empty approver name, which the escalation service raises as an exception, which this
        // class's own catch-all converts into a block. The net effect of one stray whitespace entry
        // in config would be every approval-required call refused forever, diagnosable only from a
        // per-call error log. Dropping them means a roster of ["alice", ""] still works.
        var roster = approval.Approvers
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

        if (roster.Count < approval.Approvers.Count
            && Interlocked.Exchange(ref s_blankApproversWarned, 1) == 0)
        {
            _logger.LogWarning(
                "ToolApproval:Approvers contains {Count} blank entr(ies), which were ignored. Remove them from configuration.",
                approval.Approvers.Count - roster.Count);
        }

        if (roster.Count == 0)
        {
            _logger.LogWarning(
                "Tool approval routing is enabled but no approvers are configured — tool {ToolName} blocked without raising an escalation. " +
                "Set AppConfig:AI:Governance:ToolApproval:Approvers to a non-empty roster.",
                toolName);
            return ToolApprovalResult.NotRouted("no approvers configured");
        }

        // Computed once and reused for both the recall (below) and the clear-on-explicit-denial
        // (in Interpret) — the two must agree on identity, or a retry could recall under one key
        // and clear under another. Null when the turn has no known conversation (e.g. a
        // console-hosted run with no durable identity); attempt attribution then simply
        // degrades to "always attempt 1", the same behaviour as before this feature existed.
        var failureKey = ApprovalFailureKey.TryCreate(_executionContext.ConversationId, agentId, toolName);

        EscalationRequest request;
        try
        {
            // Inside the try on purpose. Building the request renders and sanitizes model-supplied
            // arguments, so it can throw on input the model controls — a deeply nested value exceeds
            // the JSON writer's depth limit, and a host sanitizer may throw for its own reasons. The
            // class promises to fail closed at every exit; leaving construction outside the try made
            // that promise false for exactly the inputs an adversarial turn would choose.
            request = BuildRequest(agentId, toolName, reason, radius, arguments, governance, roster, failureKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not build the approval request for {ToolName} — call blocked (fail-closed).", toolName);
            return ToolApprovalResult.Denied("the approval request could not be prepared");
        }

        try
        {
            var outcome = await _escalationService
                .RequestEscalationAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return Interpret(outcome, toolName, request, failureKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The turn was abandoned while a human was deciding. Nothing ran; surface it as a block
            // rather than letting the cancellation escape and surface as a tool error.
            _logger.LogInformation(
                "Tool approval for {ToolName} (escalation {EscalationId}) was cancelled with the turn — call blocked.",
                toolName, request.EscalationId);
            return ToolApprovalResult.Denied("the turn was cancelled while awaiting approval", request.EscalationId);
        }
        catch (Exception ex)
        {
            // The one place an unavailable approval subsystem is decided. Fail closed, consistent
            // with the capability envelope and the classification gate.
            _logger.LogError(ex,
                "Tool approval escalation failed for {ToolName} (escalation {EscalationId}) — call blocked (fail-closed).",
                toolName, request.EscalationId);
            return ToolApprovalResult.Denied("the approval request could not be completed", request.EscalationId);
        }
    }

    private EscalationRequest BuildRequest(
        string agentId,
        string toolName,
        string reason,
        BlastRadius radius,
        IReadOnlyDictionary<string, object?>? arguments,
        GovernanceConfig governance,
        IReadOnlyList<string> roster,
        ApprovalFailureKey? failureKey)
    {
        var approval = governance.ToolApproval;
        var escalation = governance.Escalation;

        var strategy = ParseStrategy(escalation.DefaultApprovalStrategy);

        var priority = radius >= ParseCriticalThreshold(approval.CriticalAtBlastRadius)
            ? EscalationPriority.Critical
            : EscalationPriority.Blocking;

        // #325 retry attribution: recall a prior failed attempt at this exact (conversation, agent,
        // tool) so the next approver sees "this failed last time, here's why" instead of asking the
        // same question cold. No recall — no known conversation, first attempt, or an LRU eviction —
        // leaves the request at its record defaults (attempt 1, no prior failure).
        var recall = failureKey is { } key ? _failureMemory.TryRecall(key) : null;

        // #321 revision round: recall a prior Revise verdict at this same key so the round counter
        // actually advances and the next approver sees what was already asked for. A failure
        // recall and a revision recall CAN both be live at once — a failed approved attempt (#325)
        // followed by a fresh escalation for the retry that itself gets revised — since RecordFailure
        // never touches revision state. See IApprovalFailureMemory.ClearRevision's remarks.
        var revisionRecall = failureKey is { } revKey ? _failureMemory.TryRecallRevision(revKey) : null;

        return new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = agentId,
            ToolName = toolName,
            Arguments = SanitizeArguments(toolName, arguments),
            Description = $"Agent '{agentId}' is attempting to call tool '{toolName}'. {reason}",
            RiskLevel = radius.ToRiskLevel(),
            Priority = priority,
            ApprovalStrategy = strategy,
            QuorumThreshold = QuorumFor(strategy, roster.Count),
            Approvers = [.. roster],
            TimeoutSeconds = approval.TimeoutSeconds ?? escalation.DefaultTimeoutSeconds,
            TimeoutAction = TimeoutAction,
            RequestedAt = DateTimeOffset.UtcNow,
            AttemptNumber = recall is { } r ? r.PriorAttemptCount + 1 : 1,
            PriorFailureReason = recall is { } r2
                ? ClampToConfiguredLength(
                    r2.FailureReason,
                    escalation.RetryAttribution.MaxPriorFailureLength,
                    EscalationRequestInvariants.MaxPriorFailureReasonLength)
                : null,
            // #472: carried alongside the clamped text, not clamped itself — an enum has no length
            // to bound.
            PriorFailureReasonSubstitution = recall is { } r3 ? r3.Substitution : null,
            // Revision wins when both are live: ClearRevision fires the instant any escalation for
            // this key is approved, so a failure can only ever be recorded (which requires an
            // approval to have happened first) *before* a still-live revision recall, never after.
            // The revision recall is therefore always the more recent of the two — the true
            // immediate predecessor, keeping the audit chain a single linked list rather than a
            // fork.
            PredecessorEscalationId = revisionRecall?.EscalationId ?? recall?.EscalationId,
            RevisionRound = revisionRecall?.RevisionRound ?? 1,
            PriorRevisionInstructions = revisionRecall?.Instructions
        };
    }

    /// <summary>
    /// Interprets an escalation outcome as a routed result: clears or advances the retry/revision
    /// memory for this action, and — on a Revise verdict — composes the model-facing carve-out
    /// text when the operator has opted in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure-attribution memory (#325) is cleared on <see cref="EscalationResolutionType.Denied"/>
    /// alone, never on <see cref="EscalationResolutionType.TimedOut"/> or
    /// <see cref="EscalationResolutionType.Escalated"/>. "The reviewer ended this line of retries"
    /// presupposes a reviewer actually looked; a timeout means nobody did, and erasing the context
    /// the next approver needs on a mere timeout would invert the feature this memory exists for.
    /// </para>
    /// <para>
    /// Revision memory (#321) follows a different rule: it clears on <em>either</em> Approved or
    /// Denied — the revise conversation is over at that decision, not at whatever an approved call
    /// later does — and advances (never clears) on Revised, independently of whether the model-facing
    /// relay is turned on. Round-tracking has to work unconditionally, or the configured round cap
    /// never actually bites: <see cref="EscalationRequestInvariants"/>'s ceiling only means anything
    /// if <c>EscalationRequest.RevisionRound</c> is genuinely threaded from one escalation to the
    /// next, which is this method's job, not the config gate's.
    /// </para>
    /// <para>
    /// Reads <see cref="_governanceConfig"/> fresh here rather than reusing the snapshot
    /// <see cref="RequestApprovalAsync"/> captured before raising the escalation: this method only
    /// runs after the human decision, potentially minutes later on a Blocking-priority request, and
    /// the relay gate + roster check both need to see a config change an operator made while the
    /// call was waiting — an approver revoked mid-wait, or the relay switched off mid-wait, must
    /// take effect immediately rather than only on the next call.
    /// </para>
    /// </remarks>
    private ToolApprovalResult Interpret(
        EscalationOutcome outcome, string toolName, EscalationRequest request, ApprovalFailureKey? failureKey)
    {
        var escalationId = request.EscalationId;

        if (outcome.IsApproved)
        {
            // Named deliberately: an approved consequential action must be attributable to the
            // people who approved it, in the same log line that records it proceeding.
            // An outcome can resolve approved with no recorded decisions (an administrative
            // force-approve, or a rehydrated outcome). Naming the resolution beats naming nobody:
            // an approved consequential action must never be recorded as attributable to "".
            var named = outcome.Decisions
                .Where(d => d.IsApproved)
                .Select(d => d.ApproverName)
                .ToList();
            var approvers = named.Count > 0
                ? string.Join(", ", named)
                : $"no named approver ({outcome.ResolutionType})";
            _logger.LogInformation(
                "Tool {ToolName} approved by [{Approvers}] (escalation {EscalationId}, resolution {Resolution}) — call proceeding.",
                toolName, approvers, escalationId, outcome.ResolutionType);

            // The revise conversation, if there was one, ends here — an approval is a decision,
            // not a mid-conversation state. #325's failure memory is untouched: whether this
            // approved call goes on to succeed or fail is not decided yet.
            if (failureKey is { } approvedKey)
                _failureMemory.ClearRevision(approvedKey);

            return ToolApprovalResult.Approved($"approved by {approvers}", escalationId);
        }

        _logger.LogWarning(
            "Tool {ToolName} was not approved (escalation {EscalationId}, resolution {Resolution}) — call blocked.",
            toolName, escalationId, outcome.ResolutionType);

        var why = outcome.ResolutionType switch
        {
            EscalationResolutionType.TimedOut => "no approver responded within the timeout",
            EscalationResolutionType.Escalated => "escalated to a higher authority tier without approval",
            EscalationResolutionType.Revised => "an approver asked for the call to be revised",
            _ => "an approver refused the call"
        };

        // A Denied resolution — including a Revise verdict that degraded to Denied at the round
        // cap (DefaultEscalationService.ResolveResolutionType) — removes the whole cache entry,
        // which ends both the #325 failure chain and any #321 revision chain for this key in one
        // step. There is nothing left to revise once a human has said no.
        if (outcome.ResolutionType == EscalationResolutionType.Denied && failureKey is { } key)
            _failureMemory.Clear(key);

        if (outcome.ResolutionType != EscalationResolutionType.Revised)
            return ToolApprovalResult.Denied(why, escalationId);

        return InterpretRevised(outcome, toolName, request, failureKey, _governanceConfig.CurrentValue, why);
    }

    /// <summary>
    /// Text stored in revision memory and shown to the next approver when sanitization removes a
    /// reviewer's instructions entirely. Never relayed to the model — see the
    /// <c>!sanitizedIsUsable</c> check in <see cref="InterpretRevised"/>.
    /// </summary>
    private const string RedactedInstructionsPlaceholder =
        "(the reviewer's instructions were removed by content safety and could not be recorded)";

    /// <summary>
    /// The #321 carve-out: advances revision memory unconditionally whenever a live conversation
    /// can track it, and — only when <c>ToolApproval.RelayRevisionInstructionsToModel</c> is on —
    /// composes the attributed, delimited, length-bounded text that becomes the refused call's
    /// model-facing message.
    /// </summary>
    private ToolApprovalResult InterpretRevised(
        EscalationOutcome outcome, string toolName, EscalationRequest request,
        ApprovalFailureKey? failureKey, GovernanceConfig governance, string why)
    {
        var escalationId = request.EscalationId;

        // The round cap is one of the containments this whole carve-out leans on
        // (EscalationConfig.Revision.MaxRounds, enforced by DefaultEscalationService reading
        // EscalationRequest.RevisionRound). That counter only ever advances through this key —
        // with none, BuildRequest can never see a prior round, every subsequent revise on this
        // action resolves as round 1 forever, and the cap can never fire. Refuse the carve-out
        // entirely rather than open a channel this class cannot bound; the call still blocks
        // exactly as an ordinary denial would, same as before this feature existed. Checked before
        // composing anything, since nothing below can matter without a key to record it under.
        if (failureKey is not { } key)
        {
            _logger.LogWarning(
                "Tool {ToolName} revise verdict (escalation {EscalationId}) has no conversation to key revision " +
                "memory on — the round cap cannot be enforced, so instructions are not relayed to the model.",
                toolName, escalationId);
            return ToolApprovalResult.Denied(why, escalationId);
        }

        var composed = ComposeRevisionFeedback(outcome, governance);

        // Composed from ApproverDecision.Instructions — human-authored, not model-influenced —
        // but run through the same sanitizer chain that scrubs tool output anyway: a reviewer can
        // paste text they did not write (a secret, a copied prompt-injection payload), and this is
        // a channel deliberately being opened into model context. Matches the precedent
        // MagenticHitlBridge already set for the harness's other human-to-model relay.
        var sanitized = composed is { } feedback
            ? _sanitizer.Sanitize(feedback.Instructions, toolName).SanitizedContent
            : null;
        var sanitizedIsUsable = !string.IsNullOrWhiteSpace(sanitized);

        // Round-tracking must advance even when there is nothing usable to relay — composed is
        // null (no roster-valid Revise decision survived the live-config check; e.g. the one
        // approver who voted Revise was revoked from ToolApproval:Approvers before this ran) just
        // as much as when sanitization redacts everything. Gating RecordRevision on either would
        // let a revoked-approver race, or a reviewer whose feedback keeps triggering redaction,
        // revise forever without ever reaching MaxRounds — the exact unbounded-loop failure this
        // cap exists to prevent. A placeholder satisfies RecordRevision's non-blank contract and
        // gives the next approver an honest signal instead of silence.
        //
        // Same value passed twice below on purpose, not a copy-paste slip: there is no separate
        // operator-configured soft cap for what gets stored in revision memory (unlike the relay
        // clamp further down, which has one) — this is enforcing only the hard invariant ceiling.
        var stored = ClampToConfiguredLength(
            sanitizedIsUsable ? sanitized! : RedactedInstructionsPlaceholder,
            EscalationRequestInvariants.MaxRevisionInstructionsLength,
            EscalationRequestInvariants.MaxRevisionInstructionsLength);
        _failureMemory.RecordRevision(key, request.RevisionRound + 1, stored, escalationId);

        if (composed is not { } usable || !sanitizedIsUsable || !governance.ToolApproval.RelayRevisionInstructionsToModel)
            return ToolApprovalResult.Denied(why, escalationId);

        // Clamped from the raw sanitized text, not from `stored` above — `stored` may already
        // carry a "… (truncated)" suffix from the 4096-char storage ceiling, and re-clamping an
        // already-truncated string down to the smaller relay ceiling can cut through that suffix
        // or append a second one. Each ceiling applies exactly once, to the same untouched source.
        var relayText = ClampToConfiguredLength(
            sanitized!,
            governance.ToolApproval.MaxRelayedInstructionsLength,
            EscalationRequestInvariants.MaxRevisionInstructionsLength);
        var wrapped = HumanFeedbackRelay.Wrap(relayText, usable.Attribution);

        if (wrapped is null)
            return ToolApprovalResult.Denied(why, escalationId);

        _logger.LogInformation(
            "Tool {ToolName} revise instructions relayed to the model (escalation {EscalationId}, from [{Attribution}]).",
            toolName, escalationId, usable.Attribution);

        return ToolApprovalResult.Revised(why, escalationId, wrapped);
    }

    /// <summary>
    /// Composes every live-roster Revise decision's raw instructions into one attributed body, or
    /// null when there is nothing usable to relay (no Revise decisions, every one left
    /// <see cref="ApproverDecision.Instructions"/> blank, or none are on the current roster).
    /// Unsanitized and unclamped — the caller owns both, because sanitizing has to happen before
    /// either the storage clamp or the relay clamp can decide anything meaningful about the final
    /// length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtered against the roster as configured <em>right now</em>
    /// (<c>GovernanceConfig.ToolApproval.Approvers</c>), intersected with the escalation's own
    /// <see cref="EscalationOutcome.Approvers"/> — not <see cref="EscalationOutcome.Approvers"/>
    /// alone. That field and <see cref="EscalationOutcome.Decisions"/> both travel on the same
    /// durable record from the same write, so checking one against the other adds no independent
    /// trust: an approver revoked from config while this escalation was pending would still pass a
    /// check against the record's own frozen copy of the roster it was raised under. Live config
    /// is the one roster a rehydrated or tampered durable record cannot have influenced.
    /// </para>
    /// <para>
    /// One contribution per approver, kept by earliest <see cref="ApproverDecision.RespondedAt"/>.
    /// <c>DefaultEscalationService</c>'s idempotency chokepoint already refuses a second decision
    /// from the same approver on the live submission path, so this is defense in depth against a
    /// rehydrated or hand-edited durable record that duplicates one.
    /// </para>
    /// </remarks>
    private static (string Attribution, string Instructions)? ComposeRevisionFeedback(
        EscalationOutcome outcome, GovernanceConfig governance)
    {
        var liveRoster = new HashSet<string>(
            governance.ToolApproval.Approvers.Where(a => !string.IsNullOrWhiteSpace(a)),
            ApproverNames.Comparer);
        liveRoster.IntersectWith(outcome.Approvers);

        var contributions = outcome.Decisions
            .Where(d => d.Verdict == ApproverVerdict.Revise
                && !string.IsNullOrWhiteSpace(d.Instructions)
                && liveRoster.Contains(d.ApproverName))
            .OrderBy(d => d.RespondedAt)
            .ThenBy(d => d.ApproverName, StringComparer.Ordinal)
            .DistinctBy(d => d.ApproverName, ApproverNames.Comparer)
            .ToList();

        if (contributions.Count == 0)
            return null;

        var attribution = string.Join(", ", contributions.Select(d => $"'{d.ApproverName}'"));
        var body = string.Join(
            "\n", contributions.Select(d => $"- {d.ApproverName}: {FlattenToOneLine(d.Instructions!)}"));

        return (attribution, body);
    }

    /// <summary>
    /// Each contribution renders as one line in the composed body ("- name: text"). Without this,
    /// a single approver's own free-text Instructions containing an embedded newline plus a fake
    /// "- otherApprover: ..." fragment would render as if a second reviewer had independently
    /// contributed — content structurally indistinguishable from a genuine second contributor.
    /// Delegates to <see cref="HumanFeedbackRelay.BlankIfLineBreak"/> rather than a private copy —
    /// see that method's remarks for why a second, independently-maintained copy is exactly how
    /// this class of check drifts (this file's own prior copy blanked only <c>\r</c>/<c>\n</c>,
    /// not every control character, which the shared version does).
    /// </summary>
    private static string FlattenToOneLine(string text) =>
        new(text.Select(HumanFeedbackRelay.BlankIfLineBreak).ToArray());

    /// <summary>
    /// Truncates <paramref name="text"/> to the configured display bound, defensively re-clamped to
    /// always stay under <paramref name="hardCeiling"/> even including the truncation suffix.
    /// </summary>
    /// <remarks>
    /// Shared by every place in this class that carries operator- or approver-facing free text
    /// forward into a new <c>EscalationRequest</c> field with its own invariant ceiling: a prior
    /// failure reason (soft cap <c>EscalationConfig.RetryAttribution.MaxPriorFailureLength</c>,
    /// hard cap <see cref="EscalationRequestInvariants.MaxPriorFailureReasonLength"/>) and composed
    /// revise instructions (soft cap <c>ToolApprovalConfig.MaxRelayedInstructionsLength</c>, hard cap
    /// <see cref="EscalationRequestInvariants.MaxRevisionInstructionsLength"/>). The respective
    /// config validators tie each soft cap to its hard cap at boot — but this clamp does not trust
    /// that validation to have run: a misconfigured soft cap above the hard cap must degrade to a
    /// shorter value, never to a request that fails <see cref="EscalationRequestInvariants"/> and
    /// gets fail-closed for a reason an operator would have no way to connect to this setting.
    /// </remarks>
    private static string ClampToConfiguredLength(string text, int configuredMaxLength, int hardCeiling)
    {
        const string suffix = "… (truncated)";
        var cap = Math.Clamp(configuredMaxLength, 1, hardCeiling - suffix.Length);

        if (text.Length <= cap)
            return text;

        // Back off one more char when the cut would land inside a surrogate pair — cutting between
        // the two UTF-16 code units of one character emits a lone surrogate into model- and
        // approver-facing text, which is invalid UTF-16 the moment anything downstream re-encodes it.
        if (cap > 0 && char.IsHighSurrogate(text[cap - 1]))
            cap--;

        return string.Concat(text.AsSpan(0, cap), suffix);
    }

    /// <summary>
    /// Renders the call arguments for the approver, scrubbed through the response-sanitizer chain
    /// and length-capped.
    /// </summary>
    /// <remarks>
    /// The arguments are the whole point of asking a human — approving "file_system" tells them
    /// nothing, approving a specific path tells them everything. But an argument set is
    /// model-influenced text that lands in a notification, a durable record, and an audit line, so
    /// it is sanitized with the same chain that scrubs tool output before it is shown anywhere.
    /// </remarks>
    private IReadOnlyDictionary<string, string> SanitizeArguments(
        string toolName, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return new Dictionary<string, string>();

        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in arguments.OrderBy(a => a.Key, StringComparer.Ordinal).Take(MaxArgumentCount))
            rendered[kvp.Key] = _sanitizer.Sanitize(Render(kvp.Value), toolName).SanitizedContent;

        if (arguments.Count > MaxArgumentCount)
            rendered["(truncated)"] = $"{arguments.Count - MaxArgumentCount} further argument(s) omitted";

        return rendered;
    }

    private static string Render(object? value)
    {
        if (value is null)
            return "null";

        string text;
        try
        {
            text = value as string ?? JsonSerializer.Serialize(value);
        }
        catch (Exception)
        {
            // Any serialization failure — an unsupported type, a cycle, or a value nested deeper
            // than the writer's depth limit — must not fail the approval request. The approver still
            // learns a value of this shape was passed. Catching broadly here is deliberate: this runs
            // on model-supplied input, so the set of reachable exceptions is not ours to enumerate.
            text = $"<unserializable {value.GetType().Name}>";
        }

        return text.Length > MaxArgumentValueLength
            ? string.Concat(text.AsSpan(0, MaxArgumentValueLength), "… (truncated)")
            : text;
    }

    /// <summary>
    /// Resolves the blast radius at or above which an approval is raised at Critical priority.
    /// </summary>
    /// <remarks>
    /// Parses by member NAME only. A bare <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
    /// accepts any integer string, including one outside the defined range: <c>"99"</c> succeeded and
    /// produced a <see cref="BlastRadius"/> of 99, which is not a member. The comparison below is
    /// <c>radius >= threshold</c>, so that value silently made it impossible for any call to reach
    /// Critical priority — the setting's entire purpose, disabled by a typo, with no warning
    /// because parsing had "succeeded" (#296).
    /// </remarks>
    private BlastRadius ParseCriticalThreshold(string configured)
    {
        if (EnumNameHelper.TryParseName<BlastRadius>(configured, out var parsed))
            return parsed;

        _logger.LogWarning(
            "ToolApproval:CriticalAtBlastRadius '{Configured}' is not a valid blast radius name — treating as Critical.",
            configured);
        return BlastRadius.Critical;
    }

    /// <summary>
    /// The N in "N of M must approve", for a Quorum roster.
    /// </summary>
    /// <remarks>
    /// Only Quorum carries a threshold; the other strategies ignore it and take zero. Leaving it at
    /// zero for Quorum is not a benign default — <c>EscalationRequestInvariants</c> requires it to
    /// fall within 1..approvers, so a host whose <c>DefaultApprovalStrategy</c> is "Quorum" (a value
    /// the escalation config validator accepts) would have had every single tool approval throw
    /// inside the escalation service and come back as a block. The feature would have been silently
    /// dead on exactly the hosts running the strictest approval policy.
    /// Simple majority is the conventional reading of a quorum and needs no additional configuration.
    /// </remarks>
    private static int QuorumFor(ApprovalStrategyType strategy, int approverCount) =>
        strategy == ApprovalStrategyType.Quorum ? (approverCount / 2) + 1 : 0;

    /// <summary>
    /// Resolves the configured approval strategy, falling back to <see cref="ApprovalStrategyType.AnyOf"/>.
    /// </summary>
    /// <remarks>
    /// Parses by member NAME only, for the same reason as <see cref="ParseCriticalThreshold"/> and
    /// with a sharper consequence. <c>DefaultEscalationService</c> resolves the strategy from
    /// <em>keyed DI</em>, using the enum value as the key. An undefined value taken from a numeric
    /// string has no registered service, so <c>GetRequiredKeyedService</c> throws — and this class's
    /// own fail-closed catch converts that into a block. One mistyped character would refuse every
    /// approval-required tool call for the life of the process, leaving nothing behind but a per-call
    /// error log. <c>EscalationRequestInvariants</c> now also rejects a request whose
    /// <c>ApprovalStrategy</c> is not a defined member — but only once a value has already reached
    /// the request, which is exactly what this fallback prevents: rejecting a bad config string
    /// here keeps the tool-call path from ever constructing a request that needs that later check.
    /// Turning the whole config-string failure into one warning and a documented default (#296).
    /// </remarks>
    private static ApprovalStrategyType ParseStrategy(string configured) =>
        EnumNameHelper.TryParseName<ApprovalStrategyType>(configured, out var parsed)
            ? parsed
            : ApprovalStrategyType.AnyOf;

    /// <summary>
    /// The timeout action for a tool-call approval, which is always a denial and is deliberately
    /// <em>not</em> read from <c>Escalation.DefaultTimeoutAction</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="EscalationTimeoutAction.Approve"/> is a legitimate global default for
    /// informational escalations, and its own documentation warns it should never reach a high
    /// priority. Inheriting it here would mean a risky tool call proceeds precisely because nobody
    /// was watching — a safety gate that fails open under load, which is the one failure mode this
    /// gate exists to prevent. A host that wants unattended tool calls to proceed should leave
    /// <c>ToolApproval.Enabled</c> off rather than configure the gate to approve itself.
    /// </remarks>
    private static EscalationTimeoutAction TimeoutAction => EscalationTimeoutAction.DenyAndEscalate;
}

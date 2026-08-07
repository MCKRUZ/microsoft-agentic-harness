using System.Text.Json;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
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
        ILogger<EscalationToolApprovalRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(escalationService);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(governanceConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _escalationService = escalationService;
        _sanitizer = sanitizer;
        _governanceConfig = governanceConfig;
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

        EscalationRequest request;
        try
        {
            // Inside the try on purpose. Building the request renders and sanitizes model-supplied
            // arguments, so it can throw on input the model controls — a deeply nested value exceeds
            // the JSON writer's depth limit, and a host sanitizer may throw for its own reasons. The
            // class promises to fail closed at every exit; leaving construction outside the try made
            // that promise false for exactly the inputs an adversarial turn would choose.
            request = BuildRequest(agentId, toolName, reason, radius, arguments, governance, roster);
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

            return Interpret(outcome, toolName, request.EscalationId);
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
        IReadOnlyList<string> roster)
    {
        var approval = governance.ToolApproval;
        var escalation = governance.Escalation;

        var strategy = ParseStrategy(escalation.DefaultApprovalStrategy);

        var priority = radius >= ParseCriticalThreshold(approval.CriticalAtBlastRadius)
            ? EscalationPriority.Critical
            : EscalationPriority.Blocking;

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
            RequestedAt = DateTimeOffset.UtcNow
        };
    }

    private ToolApprovalResult Interpret(EscalationOutcome outcome, string toolName, Guid escalationId)
    {
        if (outcome.IsApproved)
        {
            // Named deliberately: an approved consequential action must be attributable to the
            // people who approved it, in the same log line that records it proceeding.
            // An outcome can resolve approved with no recorded decisions (an administrative
            // force-approve, or a rehydrated outcome). Naming the resolution beats naming nobody:
            // an approved consequential action must never be recorded as attributable to "".
            var named = outcome.Decisions.Where(d => d.Approved).Select(d => d.ApproverName).ToList();
            var approvers = named.Count > 0
                ? string.Join(", ", named)
                : $"no named approver ({outcome.ResolutionType})";
            _logger.LogInformation(
                "Tool {ToolName} approved by [{Approvers}] (escalation {EscalationId}, resolution {Resolution}) — call proceeding.",
                toolName, approvers, escalationId, outcome.ResolutionType);
            return ToolApprovalResult.Approved($"approved by {approvers}", escalationId);
        }

        _logger.LogWarning(
            "Tool {ToolName} was not approved (escalation {EscalationId}, resolution {Resolution}) — call blocked.",
            toolName, escalationId, outcome.ResolutionType);

        var why = outcome.ResolutionType switch
        {
            EscalationResolutionType.TimedOut => "no approver responded within the timeout",
            EscalationResolutionType.Escalated => "escalated to a higher authority tier without approval",
            _ => "an approver refused the call"
        };

        return ToolApprovalResult.Denied(why, escalationId);
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

    private BlastRadius ParseCriticalThreshold(string configured)
    {
        if (Enum.TryParse<BlastRadius>(configured, ignoreCase: true, out var parsed))
            return parsed;

        _logger.LogWarning(
            "ToolApproval:CriticalAtBlastRadius '{Configured}' is not a valid blast radius — treating as Critical.",
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

    private static ApprovalStrategyType ParseStrategy(string configured) =>
        Enum.TryParse<ApprovalStrategyType>(configured, ignoreCase: true, out var parsed)
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

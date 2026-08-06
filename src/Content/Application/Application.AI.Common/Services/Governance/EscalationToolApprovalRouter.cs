using System.Text.Json;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Changes;
using Domain.AI.Escalation;
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
        if (!governance.Escalation.Enabled)
            return ToolApprovalResult.NotRouted("escalation subsystem is disabled");

        // An escalation with nobody on the roster can never be answered — it would stall the turn
        // until it timed out and then block anyway. Refuse immediately instead, and say why.
        if (approval.Approvers.Count == 0)
        {
            _logger.LogWarning(
                "Tool approval routing is enabled but no approvers are configured — tool {ToolName} blocked without raising an escalation. " +
                "Set AppConfig:AI:Governance:ToolApproval:Approvers to a non-empty roster.",
                toolName);
            return ToolApprovalResult.NotRouted("no approvers configured");
        }

        var request = BuildRequest(agentId, toolName, reason, radius, arguments, governance);

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
        GovernanceConfig governance)
    {
        var approval = governance.ToolApproval;
        var escalation = governance.Escalation;

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
            RiskLevel = MapRisk(radius),
            Priority = priority,
            ApprovalStrategy = ParseStrategy(escalation.DefaultApprovalStrategy),
            Approvers = [.. approval.Approvers],
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
            var approvers = string.Join(", ", outcome.Decisions.Where(d => d.Approved).Select(d => d.ApproverName));
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
        catch (NotSupportedException)
        {
            // A value the serializer cannot represent must not fail the approval request; the
            // approver still learns a value of this shape was passed.
            text = $"<unserializable {value.GetType().Name}>";
        }

        return text.Length > MaxArgumentValueLength
            ? string.Concat(text.AsSpan(0, MaxArgumentValueLength), "… (truncated)")
            : text;
    }

    private static RiskLevel MapRisk(BlastRadius radius) => radius switch
    {
        BlastRadius.Trivial or BlastRadius.Low => RiskLevel.Low,
        BlastRadius.Medium => RiskLevel.Medium,
        BlastRadius.High => RiskLevel.High,
        _ => RiskLevel.Critical
    };

    private BlastRadius ParseCriticalThreshold(string configured)
    {
        if (Enum.TryParse<BlastRadius>(configured, ignoreCase: true, out var parsed))
            return parsed;

        _logger.LogWarning(
            "ToolApproval:CriticalAtBlastRadius '{Configured}' is not a valid blast radius — treating as Critical.",
            configured);
        return BlastRadius.Critical;
    }

    private ApprovalStrategyType ParseStrategy(string configured) =>
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

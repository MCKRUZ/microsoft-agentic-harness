using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolCallObserverChain"/>: consults each registered
/// <see cref="IToolCallObserver"/> in registration order and stops at the first one that objects.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Fail-closed on every abnormal exit.</strong> An observer that throws blocks the call, as
/// does one that escalates when no approval route is configured. A rule that could not run has not
/// cleared the action it exists to judge, and treating "the check crashed" as "the check passed"
/// would make the seam actively dangerous — a consumer's safety rule would silently stop applying
/// the moment it had a bug.
/// </para>
/// <para>
/// <strong>An approved escalation resumes the chain rather than ending it.</strong> A human saying
/// yes answers the observer that asked; it does not speak for the observers that have not run yet.
/// Evaluation continues, and a later observer can still block.
/// </para>
/// </remarks>
public sealed class ToolCallObserverChain : IToolCallObserverChain
{
    private readonly IReadOnlyList<IToolCallObserver> _observers;
    private readonly IToolApprovalRouter _approvalRouter;
    private readonly IToolRiskClassifier _riskClassifier;
    private readonly IAgentExecutionContext _executionContext;
    private readonly IGovernanceAuditService _auditService;
    private readonly IGovernanceTraceRecorder _trace;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly ILogger<ToolCallObserverChain> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCallObserverChain"/> class.</summary>
    /// <param name="trace">
    /// The turn's governance trail, corrected when this chain blocks a call the governor allowed.
    /// </param>
    public ToolCallObserverChain(
        IEnumerable<IToolCallObserver> observers,
        IToolApprovalRouter approvalRouter,
        IToolRiskClassifier riskClassifier,
        IAgentExecutionContext executionContext,
        IGovernanceAuditService auditService,
        IGovernanceTraceRecorder trace,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        ILogger<ToolCallObserverChain> logger)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(approvalRouter);
        ArgumentNullException.ThrowIfNull(riskClassifier);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(governanceConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _observers = [.. observers];
        _approvalRouter = approvalRouter;
        _riskClassifier = riskClassifier;
        _executionContext = executionContext;
        _auditService = auditService;
        _trace = trace;
        _governanceConfig = governanceConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool HasObservers => _observers.Count > 0;

    /// <inheritdoc />
    public async ValueTask<ToolInvocationDecision> EvaluateAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (_observers.Count == 0)
            return ToolInvocationDecision.Allow();

        var observation = new ToolCallObservation(
            toolName,
            arguments,
            _executionContext.AgentId,
            _executionContext.ConversationId,
            _executionContext.TurnNumber);

        foreach (var observer in _observers)
        {
            var verdict = await SafeObserveAsync(observer, observation, cancellationToken).ConfigureAwait(false);

            if (verdict.Outcome == ToolCallOutcome.Proceed)
                continue;

            if (verdict.Outcome == ToolCallOutcome.RequireApproval)
            {
                var approval = await RouteForApprovalAsync(
                    observer, toolName, verdict.Reason, arguments, cancellationToken).ConfigureAwait(false);

                // Approved: this observer is satisfied, but the rest have not spoken. Keep going.
                if (approval.Outcome == ToolApprovalOutcome.Approved)
                    continue;

                var block = Blocked(observer.Name, toolName, "escalated and not approved");

                // The #321 revise carve-out's second production wiring — see
                // ToolInvocationGovernor.Approval.cs for the first and the fuller explanation.
                // Applied after Blocked() has already recorded the trace/audit/log, so every other
                // observer-triggered block stays exactly as generic as before this feature existed.
                // ApplyModelFacingOverride is shared between both wiring sites.
                return approval.ApplyModelFacingOverride(block);
            }

            // Block, and anything this chain does not recognise. Falling through to a refusal means a
            // future outcome added to the enum fails closed here rather than being waved past.
            return Blocked(observer.Name, toolName, verdict.Reason ?? "blocked by observer");
        }

        return ToolInvocationDecision.Allow();
    }

    /// <summary>
    /// Runs one observer, converting a thrown exception into a block.
    /// </summary>
    /// <remarks>
    /// Cancellation is rethrown rather than swallowed: an abandoned turn is not a policy verdict,
    /// and the caller unwinds it as cancellation.
    /// </remarks>
    private async ValueTask<ToolCallVerdict> SafeObserveAsync(
        IToolCallObserver observer, ToolCallObservation observation, CancellationToken cancellationToken)
    {
        try
        {
            return await observer.ObserveAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tool call observer '{Observer}' threw while judging {ToolName} — call blocked (fail-closed).",
                observer.Name, observation.ToolName);
            return ToolCallVerdict.Block($"observer '{observer.Name}' failed");
        }
    }

    /// <summary>
    /// Puts an escalating observer's objection to a human via the configured approval workflow.
    /// </summary>
    /// <returns>
    /// The full routed result, so a caller who cares only about the yes/no can still read
    /// <see cref="ToolApprovalResult.Outcome"/> — returning just a bool discarded
    /// <see cref="ToolApprovalResult.ModelFacingInstructions"/> and silently made the #321 revise
    /// carve-out inert on every call routed through this chain.
    /// </returns>
    private async ValueTask<ToolApprovalResult> RouteForApprovalAsync(
        IToolCallObserver observer,
        string toolName,
        string? reason,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var agentId = _executionContext.AgentId;
        if (string.IsNullOrEmpty(agentId))
        {
            // No identity means no attributable requester for the approval record. Refuse rather
            // than raise an escalation nobody can account for.
            _logger.LogWarning(
                "Observer '{Observer}' escalated {ToolName} but the turn carries no agent identity — call blocked.",
                observer.Name, toolName);
            return ToolApprovalResult.Denied("no agent identity to attribute the escalation to");
        }

        var radius = _riskClassifier.Classify(toolName).Radius;
        var result = await _approvalRouter.RequestApprovalAsync(
            agentId,
            toolName,
            $"{observer.Name}: {reason ?? "requires human approval"}",
            radius,
            arguments,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == ToolApprovalOutcome.Approved)
        {
            _logger.LogInformation(
                "Observer '{Observer}' escalated {ToolName}; a human approved it ({Reason}).",
                observer.Name, toolName, result.Reason);
        }
        else
        {
            // NotRouted means the host never configured an approval route. The observer asked for
            // a human and there is none, so the call is refused — never quietly allowed.
            _logger.LogWarning(
                "Observer '{Observer}' escalated {ToolName} and it was not approved ({Reason}) — call blocked.",
                observer.Name, toolName, result.Reason);
        }

        return result;
    }

    /// <summary>
    /// Records a blocked call and returns the deny carrying the generic model-facing message.
    /// </summary>
    /// <remarks>
    /// The model is told only that the tool is not permitted — identical to every other gate's
    /// refusal. The observer's name and reason stay in the log and the audit trail, so an operator
    /// can trace which rule fired while the model learns nothing about the rule set it could probe.
    /// </remarks>
    private ToolInvocationDecision Blocked(string observerName, string toolName, string reason)
    {
        _logger.LogWarning(
            "Tool call observer '{Observer}' blocked {ToolName}: {Reason}",
            observerName, toolName, reason);

        if (_governanceConfig.CurrentValue.EnableAudit)
            _auditService.Log(_executionContext.AgentId ?? "unknown", toolName, $"observer:{observerName}:blocked");

        // Correct the turn's governance trace — see IGovernanceTraceRecorder.RecordDownstreamBlock for
        // why this is routed through the recorder rather than skipped. It corrects the trace only; the
        // audit line above is the one and only audit record for this block.
        _trace.RecordDownstreamBlock(
            toolName, $"blocked by observer '{observerName}': {reason}");

        return ToolInvocationDecision.Deny(GovernanceDenials.NotPermitted(toolName));
    }
}

using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Evaluates tool requests against declarative governance policies loaded from YAML.
/// Complements <see cref="ToolPermissionBehavior{TRequest,TResponse}"/> with policy-engine-driven
/// enforcement including rate limiting, approval workflows, and audit logging.
/// </summary>
/// <remarks>
/// <para>Pipeline position: 7 (after tool permissions at 6, before content safety at 8).</para>
/// <para>Only activates when <c>GovernanceConfig.Enabled</c> is true and policies are loaded.</para>
/// </remarks>
public sealed class GovernancePolicyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IGovernancePolicyEngine _policyEngine;
    private readonly IGovernanceAuditService _auditService;
    private readonly IAgentExecutionContext _executionContext;
    private readonly IOptionsMonitor<GovernanceConfig> _config;
    private readonly IOptionsMonitor<PermissionsConfig> _permissionsConfig;
    private readonly IToolRiskClassifier _toolRiskClassifier;
    private readonly ILogger<GovernancePolicyBehavior<TRequest, TResponse>> _logger;
    private readonly IEscalationService? _escalationService;

    public GovernancePolicyBehavior(
        IGovernancePolicyEngine policyEngine,
        IGovernanceAuditService auditService,
        IAgentExecutionContext executionContext,
        IOptionsMonitor<GovernanceConfig> config,
        IOptionsMonitor<PermissionsConfig> permissionsConfig,
        IToolRiskClassifier toolRiskClassifier,
        ILogger<GovernancePolicyBehavior<TRequest, TResponse>> logger,
        IEscalationService? escalationService = null)
    {
        _policyEngine = policyEngine;
        _auditService = auditService;
        _executionContext = executionContext;
        _config = config;
        _permissionsConfig = permissionsConfig;
        _toolRiskClassifier = toolRiskClassifier;
        _logger = logger;
        _escalationService = escalationService;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IToolRequest toolRequest)
            return await next();

        if (!_config.CurrentValue.Enabled || !_policyEngine.HasPolicies)
            return await next();

        var agentId = _executionContext.AgentId ?? "unknown";

        var decision = _policyEngine.EvaluateToolCall(agentId, toolRequest.ToolName);

        if (_config.CurrentValue.EnableAudit)
            _auditService.Log(agentId, toolRequest.ToolName, decision.Action.ToString());

        if (decision.IsAllowed)
            return await next();

        if (decision.Action == GovernancePolicyAction.RequireApproval)
            return await HandleRequireApprovalAsync(agentId, toolRequest, decision, next, cancellationToken);

        _logger.LogWarning(
            "Governance policy denied agent {AgentId} access to tool {ToolName}: {Reason} (rule: {Rule})",
            agentId, toolRequest.ToolName, decision.Reason, decision.MatchedRule);

        if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked), decision.Reason, out var blocked))
            return blocked;

        throw new InvalidOperationException($"Governance policy denied: {decision.Reason}");
    }

    private async Task<TResponse> HandleRequireApprovalAsync(
        string agentId,
        IToolRequest toolRequest,
        GovernanceDecision decision,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var escalationConfig = _config.CurrentValue.Escalation;

        if (escalationConfig?.Enabled != true || _escalationService is null)
        {
            _logger.LogWarning(
                "Escalation disabled — treating RequireApproval as denial for agent {AgentId} tool {ToolName}",
                agentId, toolRequest.ToolName);

            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked), decision.Reason, out var denied))
                return denied;

            throw new InvalidOperationException($"Governance policy denied: {decision.Reason}");
        }

        // Derive escalation severity from the tool's declared blast radius rather than a
        // fixed default, so a high-impact tool escalates harder (stricter approval strategy,
        // shorter timeout, wider notification) than a low-impact one. Unknown tools fall back
        // to the Medium-equivalent default via the classifier.
        var toolRiskLevel = _toolRiskClassifier.Classify(toolRequest.ToolName).Radius.ToRiskLevel();

        var escalationRequest = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = agentId,
            ToolName = toolRequest.ToolName,
            Arguments = new Dictionary<string, string>(),
            Description = $"Agent '{agentId}' requires approval to invoke '{toolRequest.ToolName}': {decision.Reason}",
            RiskLevel = toolRiskLevel,
            Priority = EscalationPriority.Blocking,
            ApprovalStrategy = Enum.TryParse<ApprovalStrategyType>(escalationConfig.DefaultApprovalStrategy, true, out var strategy)
                ? strategy
                : ApprovalStrategyType.AnyOf,
            Approvers = decision.Approvers ?? [],
            QuorumThreshold = 1,
            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
            TimeoutAction = Enum.TryParse<EscalationTimeoutAction>(escalationConfig.DefaultTimeoutAction, true, out var timeoutAction)
                ? timeoutAction
                : EscalationTimeoutAction.DenyAndEscalate,
            RequestedAt = DateTimeOffset.UtcNow,
            OriginatingDecision = decision
        };

        var waitBehavior = ResolveEscalationWaitBehavior();

        try
        {
            if (waitBehavior == EscalationWaitBehavior.QueueAndContinue)
            {
                var escalationId = await _escalationService.QueueEscalationAsync(escalationRequest, cancellationToken);

                _logger.LogInformation(
                    "Queued escalation {EscalationId} for agent {AgentId} tool {ToolName} (QueueAndContinue)",
                    escalationId, agentId, toolRequest.ToolName);

                if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.PendingApproval),
                        $"Escalation {escalationId} pending approval", out var pending))
                    return pending;

                throw new InvalidOperationException($"Escalation {escalationId} pending approval");
            }

            var outcome = await _escalationService.RequestEscalationAsync(escalationRequest, cancellationToken);

            if (outcome.IsApproved)
            {
                _logger.LogInformation(
                    "Escalation {EscalationId} approved for agent {AgentId} tool {ToolName}",
                    outcome.EscalationId, agentId, toolRequest.ToolName);
                return await next();
            }

            _logger.LogWarning(
                "Escalation {EscalationId} denied for agent {AgentId} tool {ToolName}",
                outcome.EscalationId, agentId, toolRequest.ToolName);

            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked),
                    $"Escalation denied: {decision.Reason}", out var blocked))
                return blocked;

            throw new InvalidOperationException($"Escalation denied: {decision.Reason}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Escalation service failed for agent {AgentId} tool {ToolName} — denying (fail-closed)",
                agentId, toolRequest.ToolName);

            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked),
                    "Escalation service unavailable", out var serviceDown))
                return serviceDown;

            throw new InvalidOperationException("Escalation service unavailable", ex);
        }
    }

    private EscalationWaitBehavior ResolveEscalationWaitBehavior()
    {
        var permissions = _permissionsConfig.CurrentValue;
        var tierKey = permissions.DefaultAutonomyLevel;

        if (permissions.TierPolicies.TryGetValue(tierKey, out var policy)
            && Enum.TryParse<EscalationWaitBehavior>(policy.EscalationBehavior, true, out var behavior))
        {
            return behavior;
        }

        return EscalationWaitBehavior.Block;
    }
}

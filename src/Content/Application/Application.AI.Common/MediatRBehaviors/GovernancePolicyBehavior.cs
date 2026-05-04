using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Domain.Common;
using Domain.Common.Config.AI;
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
    private readonly ILogger<GovernancePolicyBehavior<TRequest, TResponse>> _logger;

    public GovernancePolicyBehavior(
        IGovernancePolicyEngine policyEngine,
        IGovernanceAuditService auditService,
        IAgentExecutionContext executionContext,
        IOptionsMonitor<GovernanceConfig> config,
        ILogger<GovernancePolicyBehavior<TRequest, TResponse>> logger)
    {
        _policyEngine = policyEngine;
        _auditService = auditService;
        _executionContext = executionContext;
        _config = config;
        _logger = logger;
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

        _logger.LogWarning(
            "Governance policy denied agent {AgentId} access to tool {ToolName}: {Reason} (rule: {Rule})",
            agentId, toolRequest.ToolName, decision.Reason, decision.MatchedRule);

        if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.GovernanceBlocked), decision.Reason, out var blocked))
            return blocked;

        throw new InvalidOperationException($"Governance policy denied: {decision.Reason}");
    }
}

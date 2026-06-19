using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.Common.Exceptions.ExceptionTypes;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Enforces agent-level tool permissions for requests implementing <see cref="IToolRequest"/>.
/// Uses the 3-phase permission resolution algorithm: Deny gates -> Ask rules -> Allow rules.
/// Records denials to the <see cref="IDenialTracker"/> for rate-limiting repeated attempts.
/// </summary>
/// <remarks>
/// <para>Pipeline position: 6 (after user authorization, before validation).</para>
/// <para>Skips permission checking for non-agent contexts (AgentId is null).</para>
/// <para>
/// Decision mapping:
/// <list type="bullet">
///   <item><description><see cref="PermissionBehaviorType.Allow"/> -- proceeds to next behavior.</description></item>
///   <item><description><see cref="PermissionBehaviorType.Deny"/> -- records denial, returns <see cref="ResultFailureType.Forbidden"/>.</description></item>
///   <item><description><see cref="PermissionBehaviorType.Ask"/> -- records denial, returns <see cref="ResultFailureType.PermissionRequired"/>.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ToolPermissionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IToolPermissionService _toolPermissionService;
    private readonly IDenialTracker _denialTracker;
    private readonly ICapabilityEnforcer _capabilityEnforcer;
    private readonly IToolRiskClassifier _toolRiskClassifier;
    private readonly IAutonomyDecisionEvaluator _autonomyEvaluator;
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly IOptionsMonitor<PermissionsConfig> _permissionsConfig;
    private readonly ILogger<ToolPermissionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolPermissionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="executionContext">The ambient agent execution context.</param>
    /// <param name="toolPermissionService">The permission resolution service.</param>
    /// <param name="denialTracker">Tracks repeated denials for rate-limiting auto-deny.</param>
    /// <param name="capabilityEnforcer">Capability-based enforcement for tool resource access.</param>
    /// <param name="toolRiskClassifier">Resolves a tool's declared blast radius for the graded-autonomy gate.</param>
    /// <param name="autonomyEvaluator">Graded-autonomy evaluator that decides whether a tool's risk requires approval under the active tier.</param>
    /// <param name="sandboxConfig">Sandbox configuration providing granted capabilities.</param>
    /// <param name="permissionsConfig">Permission configuration providing the autonomy tier and graded-autonomy toggle.</param>
    /// <param name="logger">Logger for permission decision auditing.</param>
    public ToolPermissionBehavior(
        IAgentExecutionContext executionContext,
        IToolPermissionService toolPermissionService,
        IDenialTracker denialTracker,
        ICapabilityEnforcer capabilityEnforcer,
        IToolRiskClassifier toolRiskClassifier,
        IAutonomyDecisionEvaluator autonomyEvaluator,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        IOptionsMonitor<PermissionsConfig> permissionsConfig,
        ILogger<ToolPermissionBehavior<TRequest, TResponse>> logger)
    {
        _executionContext = executionContext;
        _toolPermissionService = toolPermissionService;
        _denialTracker = denialTracker;
        _capabilityEnforcer = capabilityEnforcer;
        _toolRiskClassifier = toolRiskClassifier;
        _autonomyEvaluator = autonomyEvaluator;
        _sandboxConfig = sandboxConfig;
        _permissionsConfig = permissionsConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IToolRequest toolRequest)
            return await next();

        var agentId = _executionContext.AgentId;
        if (agentId is null)
        {
            _logger.LogWarning(
                "Tool permission check skipped for {ToolName} — no AgentId in execution context",
                toolRequest.ToolName);
            return await next();
        }

        var decision = await _toolPermissionService.ResolvePermissionAsync(
            agentId, toolRequest.ToolName, cancellationToken: cancellationToken);

        // Layer the graded-autonomy risk gate on top of the rule-based decision. It can only
        // tighten (Allow → Ask/Deny), never loosen, so a high-blast-radius tool the rules
        // would allow can still be routed to approval under the active tier.
        decision = ApplyRiskGate(decision, toolRequest.ToolName);

        switch (decision.Behavior)
        {
            case PermissionBehaviorType.Allow:
            {
                var grantedCapabilities = ToolCapability.None;
                foreach (var name in _sandboxConfig.CurrentValue.DefaultGrantedCapabilities)
                {
                    if (Enum.TryParse<ToolCapability>(name, ignoreCase: true, out var cap))
                        grantedCapabilities |= cap;
                }
                var capResult = await _capabilityEnforcer.EnforceAsync(
                    toolRequest.ToolName, grantedCapabilities, ct: cancellationToken);

                if (!capResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "Agent {AgentId} capability violation for tool {ToolName}: {Reason}",
                        agentId, toolRequest.ToolName, capResult.Errors[0]);

                    if (ResultHelper.TryCreateFailure<TResponse>(
                            nameof(Result.Forbidden), capResult.Errors[0], out var capForbidden))
                        return capForbidden;

                    throw new ForbiddenAccessException(capResult.Errors[0]);
                }

                return await next();
            }

            case PermissionBehaviorType.Deny:
            {
                _denialTracker.RecordDenial(agentId, toolRequest.ToolName);

                _logger.LogWarning(
                    "Agent {AgentId} denied access to tool {ToolName}: {Reason}",
                    agentId, toolRequest.ToolName, decision.Reason);

                if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.Forbidden), decision.Reason, out var forbiddenResult))
                    return forbiddenResult;

                throw new ForbiddenAccessException(decision.Reason);
            }

            case PermissionBehaviorType.Ask:
            {
                _denialTracker.RecordDenial(agentId, toolRequest.ToolName);

                _logger.LogInformation(
                    "Agent {AgentId} requires permission confirmation for tool {ToolName}: {Reason}",
                    agentId, toolRequest.ToolName, decision.Reason);

                if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.PermissionRequired), decision.Reason, out var askResult))
                    return askResult;

                throw new ForbiddenAccessException(decision.Reason);
            }

            default:
                throw new InvalidOperationException($"Unexpected permission behavior: {decision.Behavior}");
        }
    }

    /// <summary>
    /// Applies the graded-autonomy risk gate to an otherwise-<see cref="PermissionBehaviorType.Allow"/>
    /// decision. When graded autonomy is enabled, a tool whose blast radius the active tier will not
    /// auto-approve is tightened to <see cref="PermissionBehaviorType.Ask"/> (RequiresApproval) or
    /// <see cref="PermissionBehaviorType.Deny"/> (Forbidden). Non-Allow decisions and the
    /// graded-autonomy-disabled case pass through unchanged — the gate never loosens a decision.
    /// </summary>
    private PermissionDecision ApplyRiskGate(PermissionDecision decision, string toolName)
    {
        // Only an Allow can be tightened; Deny/Ask are already at least as strict.
        if (decision.Behavior != PermissionBehaviorType.Allow)
            return decision;

        var permissions = _permissionsConfig.CurrentValue;

        // Off by default: when graded autonomy is disabled the gate is a no-op, so enabling
        // tool risk classification alone changes no behavior.
        if (!permissions.GradedAutonomy.Enabled)
            return decision;

        if (!Enum.TryParse<AutonomyLevel>(permissions.DefaultAutonomyLevel, ignoreCase: true, out var tier))
        {
            _logger.LogWarning(
                "Graded autonomy is enabled but DefaultAutonomyLevel '{Tier}' is not a valid AutonomyLevel — " +
                "skipping the risk gate for tool {ToolName}.",
                permissions.DefaultAutonomyLevel, toolName);
            return decision;
        }

        var profile = _toolRiskClassifier.Classify(toolName);

        // The evaluator ignores targetKind (reserved); tool calls pass Unspecified. isStateChange
        // is the inverse of the tool's read-only flag. skillKey is null because the executing skill
        // is not exposed on the tool-permission context, so tool-call risk is evaluated at the
        // BASELINE tier. A per-skill tier *narrowing* is therefore not applied here — that only
        // forgoes extra tightening, it cannot loosen: the gate still never relaxes the rule-based
        // Allow, and the rule layer (which does see plugin/skill rules) has already run.
        var result = _autonomyEvaluator.Evaluate(
            tier, profile.Radius, ChangeTargetKind.Unspecified, isStateChange: !profile.IsReadOnly, skillKey: null);

        return result.Decision switch
        {
            AutonomyDecision.AutoApprove => decision,
            AutonomyDecision.RequiresApproval => PermissionDecision.Ask(
                $"Graded autonomy: tool '{toolName}' (blast radius {profile.Radius}) requires approval under tier {tier}. {result.Reason}"),
            AutonomyDecision.Forbidden => PermissionDecision.Deny(
                $"Graded autonomy: tool '{toolName}' (blast radius {profile.Radius}) is forbidden under tier {tier}. {result.Reason}"),
            _ => decision
        };
    }
}

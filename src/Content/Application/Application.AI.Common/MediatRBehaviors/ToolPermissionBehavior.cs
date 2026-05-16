using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.Common.Exceptions.ExceptionTypes;
using Domain.AI.Permissions;
using Domain.AI.Sandbox;
using Domain.Common;
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
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly ILogger<ToolPermissionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolPermissionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="executionContext">The ambient agent execution context.</param>
    /// <param name="toolPermissionService">The permission resolution service.</param>
    /// <param name="denialTracker">Tracks repeated denials for rate-limiting auto-deny.</param>
    /// <param name="capabilityEnforcer">Capability-based enforcement for tool resource access.</param>
    /// <param name="sandboxConfig">Sandbox configuration providing granted capabilities.</param>
    /// <param name="logger">Logger for permission decision auditing.</param>
    public ToolPermissionBehavior(
        IAgentExecutionContext executionContext,
        IToolPermissionService toolPermissionService,
        IDenialTracker denialTracker,
        ICapabilityEnforcer capabilityEnforcer,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        ILogger<ToolPermissionBehavior<TRequest, TResponse>> logger)
    {
        _executionContext = executionContext;
        _toolPermissionService = toolPermissionService;
        _denialTracker = denialTracker;
        _capabilityEnforcer = capabilityEnforcer;
        _sandboxConfig = sandboxConfig;
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
            return await next();

        var decision = await _toolPermissionService.ResolvePermissionAsync(
            agentId, toolRequest.ToolName, cancellationToken: cancellationToken);

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
                var requestedPaths = (toolRequest as IResourceScopedToolRequest)?.RequestedPaths;
                var requestedHosts = (toolRequest as IResourceScopedToolRequest)?.RequestedHosts;

                var capResult = await _capabilityEnforcer.EnforceAsync(
                    toolRequest.ToolName, grantedCapabilities,
                    requestedPaths, requestedHosts, cancellationToken);

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
}

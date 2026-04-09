using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Permissions;
using Application.Common.Exceptions.ExceptionTypes;
using Domain.AI.Permissions;
using Domain.Common;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<ToolPermissionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolPermissionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="executionContext">The ambient agent execution context.</param>
    /// <param name="toolPermissionService">The permission resolution service.</param>
    /// <param name="denialTracker">Tracks repeated denials for rate-limiting auto-deny.</param>
    /// <param name="logger">Logger for permission decision auditing.</param>
    public ToolPermissionBehavior(
        IAgentExecutionContext executionContext,
        IToolPermissionService toolPermissionService,
        IDenialTracker denialTracker,
        ILogger<ToolPermissionBehavior<TRequest, TResponse>> logger)
    {
        _executionContext = executionContext;
        _toolPermissionService = toolPermissionService;
        _denialTracker = denialTracker;
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
                return await next();

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

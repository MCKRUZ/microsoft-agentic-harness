using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.Common.Exceptions.ExceptionTypes;
using Domain.Common;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Enforces agent-level tool permissions for requests implementing <see cref="IToolRequest"/>.
/// While <c>AuthorizationBehavior</c> checks user roles/policies,
/// this behavior checks whether a specific agent is allowed to use a specific tool
/// based on the agent's manifest.
/// </summary>
/// <remarks>
/// Pipeline position: 6 (after user authorization, before validation).
/// Skips permission checking for non-agent contexts (AgentId is null).
/// </remarks>
public sealed class ToolPermissionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IToolPermissionService _toolPermissionService;
    private readonly ILogger<ToolPermissionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolPermissionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public ToolPermissionBehavior(
        IAgentExecutionContext executionContext,
        IToolPermissionService toolPermissionService,
        ILogger<ToolPermissionBehavior<TRequest, TResponse>> logger)
    {
        _executionContext = executionContext;
        _toolPermissionService = toolPermissionService;
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

        var allowed = await _toolPermissionService.IsToolAllowedAsync(
            agentId, toolRequest.ToolName, cancellationToken);

        if (!allowed)
        {
            var reason = $"Agent '{agentId}' does not have permission to use tool '{toolRequest.ToolName}'.";
            _logger.LogWarning("Agent {AgentId} denied access to tool {ToolName}",
                agentId, toolRequest.ToolName);
            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.Forbidden), reason, out var forbiddenResult))
                return forbiddenResult;
            throw new ForbiddenAccessException(reason);
        }

        return await next();
    }
}

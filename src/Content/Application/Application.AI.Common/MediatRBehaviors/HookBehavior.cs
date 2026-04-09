using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.MediatR;
using Application.Common.Exceptions.ExceptionTypes;
using Domain.AI.Hooks;
using Domain.Common;
using Domain.Common.Helpers;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Fires lifecycle hooks around tool and agent-turn requests.
/// For <see cref="IToolRequest"/>: fires <see cref="HookEvent.PreToolUse"/> before and
/// <see cref="HookEvent.PostToolUse"/> after the handler.
/// For <see cref="IAgentScopedRequest"/>: fires <see cref="HookEvent.PreTurn"/> before and
/// <see cref="HookEvent.PostTurn"/> after the handler.
/// </summary>
/// <remarks>
/// Pipeline position: 6 (after <c>ToolPermissionBehavior</c>).
/// If any PreToolUse hook returns <c>Continue = false</c>, the pipeline is short-circuited
/// with a <see cref="ResultFailureType.Forbidden"/> result.
/// </remarks>
public sealed class HookBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IHookExecutor _hookExecutor;
    private readonly IAgentExecutionContext _executionContext;
    private readonly ILogger<HookBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HookBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="hookExecutor">The hook executor for firing lifecycle hooks.</param>
    /// <param name="executionContext">The current agent execution context.</param>
    /// <param name="logger">Logger for hook execution diagnostics.</param>
    public HookBehavior(
        IHookExecutor hookExecutor,
        IAgentExecutionContext executionContext,
        ILogger<HookBehavior<TRequest, TResponse>> logger)
    {
        _hookExecutor = hookExecutor;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var isToolRequest = request is IToolRequest;
        var isAgentScoped = request is IAgentScopedRequest;

        if (!isToolRequest && !isAgentScoped)
            return await next();

        if (isToolRequest)
            return await HandleToolRequestAsync(request, next, cancellationToken);

        return await HandleAgentTurnAsync(request, next, cancellationToken);
    }

    private async Task<TResponse> HandleToolRequestAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var toolRequest = (IToolRequest)request;
        var context = BuildContext(HookEvent.PreToolUse, toolRequest.ToolName);

        var preResults = await _hookExecutor.ExecuteHooksAsync(
            HookEvent.PreToolUse, context, cancellationToken);

        var blockingResult = FindBlockingResult(preResults);
        if (blockingResult is not null)
        {
            var reason = blockingResult.StopReason
                ?? $"Hook blocked execution of tool '{toolRequest.ToolName}'.";
            _logger.LogWarning(
                "PreToolUse hook blocked tool {ToolName}: {Reason}",
                toolRequest.ToolName, reason);

            if (ResultHelper.TryCreateFailure<TResponse>(
                    nameof(Result.Forbidden), reason, out var forbiddenResult))
                return forbiddenResult;

            throw new ForbiddenAccessException(reason);
        }

        LogModifiedInputs(preResults, toolRequest.ToolName);

        var response = await next();

        var postContext = BuildContext(HookEvent.PostToolUse, toolRequest.ToolName);
        await _hookExecutor.ExecuteHooksAsync(
            HookEvent.PostToolUse, postContext, cancellationToken);

        return response;
    }

    private async Task<TResponse> HandleAgentTurnAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var context = BuildContext(HookEvent.PreTurn);

        await _hookExecutor.ExecuteHooksAsync(
            HookEvent.PreTurn, context, cancellationToken);

        var response = await next();

        var postContext = BuildContext(HookEvent.PostTurn);
        await _hookExecutor.ExecuteHooksAsync(
            HookEvent.PostTurn, postContext, cancellationToken);

        return response;
    }

    private HookExecutionContext BuildContext(HookEvent hookEvent, string? toolName = null)
    {
        return new HookExecutionContext
        {
            Event = hookEvent,
            AgentId = _executionContext.AgentId,
            ConversationId = _executionContext.ConversationId,
            TurnNumber = _executionContext.TurnNumber,
            ToolName = toolName
        };
    }

    private static HookResult? FindBlockingResult(IReadOnlyList<HookResult> results)
    {
        for (var i = 0; i < results.Count; i++)
        {
            if (!results[i].Continue)
                return results[i];
        }

        return null;
    }

    private void LogModifiedInputs(IReadOnlyList<HookResult> results, string toolName)
    {
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].ModifiedInput is not null)
            {
                _logger.LogInformation(
                    "PreToolUse hook returned ModifiedInput for tool {ToolName}. " +
                    "Input modification is not yet applied -- logging only.",
                    toolName);
            }
        }
    }
}

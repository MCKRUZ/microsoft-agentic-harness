using Application.Common.Interfaces.Agent;
using Application.Common.Interfaces.MediatR;
using Application.Common.Logging;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Extracts agent identity from requests implementing <see cref="IAgentScopedRequest"/>
/// and pushes it onto the logging scope via <see cref="AgentLogScope"/> and the scoped
/// <see cref="IAgentExecutionContext"/>.
/// </summary>
/// <remarks>
/// Pipeline position: 3 (after timeout, before audit). Every downstream behavior,
/// handler, and service automatically gets structured logging with AgentId,
/// ConversationId, and TurnNumber without manual <c>BeginScope</c> calls.
/// </remarks>
public sealed class AgentContextPropagationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly ILogger<AgentContextPropagationBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentContextPropagationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public AgentContextPropagationBehavior(
        IAgentExecutionContext executionContext,
        ILogger<AgentContextPropagationBehavior<TRequest, TResponse>> logger)
    {
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IAgentScopedRequest agentRequest)
            return await next();

        _executionContext.Initialize(
            agentRequest.AgentId,
            agentRequest.ConversationId,
            agentRequest.TurnNumber);

        using (_logger.BeginScope(new AgentLogScope(
            AgentId: agentRequest.AgentId,
            ConversationId: agentRequest.ConversationId,
            TurnNumber: agentRequest.TurnNumber)))
        {
            return await next();
        }
    }
}

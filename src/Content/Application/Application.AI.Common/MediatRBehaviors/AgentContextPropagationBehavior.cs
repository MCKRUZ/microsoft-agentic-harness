using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.Common.Logging;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Extracts agent identity from requests implementing <see cref="IAgentScopedRequest"/>
/// and pushes it onto the logging scope via <see cref="ExecutionScope"/> and the scoped
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
    private readonly IAgentTelemetryAttribution _attribution;
    private readonly ILogger<AgentContextPropagationBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentContextPropagationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public AgentContextPropagationBehavior(
        IAgentExecutionContext executionContext,
        IAgentTelemetryAttribution attribution,
        ILogger<AgentContextPropagationBehavior<TRequest, TResponse>> logger)
    {
        _executionContext = executionContext;
        _attribution = attribution;
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
            agentRequest.TurnNumber,
            // The durable conversation IS the right call-once scope for an agent turn — this is
            // the one caller where ConversationId already means exactly what CallOnceScopeId needs.
            callOnceScopeId: agentRequest.ConversationId);

        // Publishes the running agent's external governance identity for the whole turn. Must wrap
        // next(): the values have to be in effect while the turn's spans are created, because an
        // exporter reading them later runs on a background thread where ambient context is empty.
        // A no-op unless a host has opted into an agent-governance integration.
        using var attribution = _attribution.BeginTurn(
            agentRequest.AgentId,
            agentRequest.ConversationId);

        using (_logger.BeginScope(new ExecutionScope(
            ExecutorId: agentRequest.AgentId,
            CorrelationId: agentRequest.ConversationId,
            StepNumber: agentRequest.TurnNumber)))
        {
            return await next();
        }
    }
}

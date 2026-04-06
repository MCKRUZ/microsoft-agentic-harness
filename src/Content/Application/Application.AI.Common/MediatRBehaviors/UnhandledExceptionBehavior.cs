using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.Agent;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Pipeline behavior that catches any exception escaping all other behaviors,
/// enriches the log entry with agent context, and rethrows. Acts as the outermost
/// safety net in the MediatR pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This behavior should be registered <strong>first</strong> in the pipeline
/// (outermost) so it wraps all other behaviors. Exceptions that are already
/// handled by inner behaviors (e.g., validation → <c>Result&lt;T&gt;.ValidationFailure</c>)
/// never reach this behavior.
/// </para>
/// <para>
/// Unlike the Presentation-layer exception filter, this behavior has access to the
/// full MediatR request type and agent execution context, enabling richer structured
/// logging for debugging agent failures.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The MediatR request type.</typeparam>
/// <typeparam name="TResponse">The MediatR response type.</typeparam>
public sealed class UnhandledExceptionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly string RequestTypeName = typeof(TRequest).Name;

    private readonly ILogger<UnhandledExceptionBehavior<TRequest, TResponse>> _logger;
    private readonly IAgentExecutionContext _agentContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnhandledExceptionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="logger">Logger for recording unhandled exceptions.</param>
    /// <param name="agentContext">Scoped agent execution context for enrichment.</param>
    public UnhandledExceptionBehavior(
        ILogger<UnhandledExceptionBehavior<TRequest, TResponse>> logger,
        IAgentExecutionContext agentContext)
    {
        _logger = logger;
        _agentContext = agentContext;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (Exception ex)
        {
            if (_agentContext.IsActive())
            {
                _logger.LogError(ex,
                    "Unhandled exception in {RequestName}. Agent: {AgentDisplay}",
                    RequestTypeName,
                    _agentContext.GetDisplayIdentifier());
            }
            else
            {
                _logger.LogError(ex,
                    "Unhandled exception in {RequestName}",
                    RequestTypeName);
            }

            throw;
        }
    }
}

using Application.AI.Common.Exceptions;
using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.Agent;
using Application.Common.Exceptions.ExceptionTypes;
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
/// <para>
/// <strong>An expected rejection is not logged as an error (#963).</strong> A screening refusal
/// (<see cref="IScreeningRefusal"/>) or a <see cref="ValidationException"/> is the pipeline working, not
/// failing, so a burst of hostile or oversized input must not raise error-rate alerts. A refusal that is a
/// verdict on the text, and a validation rejection, are logged at Information without a stack trace; a
/// screening outage, which says nothing about the text but does mean the screening service is down, is
/// logged at Warning with its exception. Everything is still rethrown. The request's own validation runs in
/// <c>RequestValidationBehavior</c>, which is registered earlier and so wraps this behavior; a validation
/// rejection reaches here only from a request this one sends on.
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
        catch (Exception ex) when (ex is IScreeningRefusal or ValidationException)
        {
            LogExpectedRejection(ex);
            throw;
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

    private void LogExpectedRejection(Exception ex)
    {
        var agent = _agentContext.IsActive() ? _agentContext.GetDisplayIdentifier() : null;

        if (ex is IScreeningRefusal { IsVerdict: false })
        {
            _logger.LogWarning(ex,
                "{RequestName} was refused because the text could not be screened. Agent: {AgentDisplay}",
                RequestTypeName, agent);
            return;
        }

        _logger.LogInformation(
            "{RequestName} was rejected ({RejectionType}). Agent: {AgentDisplay}",
            RequestTypeName, ex.GetType().Name, agent);
    }
}

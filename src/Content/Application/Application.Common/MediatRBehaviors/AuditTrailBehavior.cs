using Application.Common.Exceptions.ExceptionTypes;
using Application.Common.Interfaces.Agent;
using Application.Common.Interfaces.MediatR;
using Application.Common.Models;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Records structured audit entries for requests implementing <see cref="IAuditable"/>.
/// Captures who (AgentId), what (request type + action), when (timestamp),
/// and outcome (success/failure/denied) for compliance and traceability.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: 4 (after context propagation so audit entries include agent identity,
/// before authorization so denied requests are still audited).
/// </para>
/// <para>
/// Audit sink failures are caught and logged — they never alter the observable outcome
/// of the request. A failed audit write does not turn a successful handler result into a failure.
/// </para>
/// </remarks>
public sealed class AuditTrailBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditTrailBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditTrailBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public AuditTrailBehavior(
        IAgentExecutionContext executionContext,
        IAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<AuditTrailBehavior<TRequest, TResponse>> logger)
    {
        _executionContext = executionContext;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IAuditable auditable)
            return await next();

        var entry = new AuditEntry
        {
            RequestType = typeof(TRequest).Name,
            Action = auditable.AuditAction,
            AgentId = _executionContext.AgentId,
            ConversationId = _executionContext.ConversationId,
            TurnNumber = _executionContext.TurnNumber,
            Timestamp = _timeProvider.GetUtcNow(),
            Outcome = AuditOutcome.Success,
            Metadata = auditable.AuditMetadata
        };

        try
        {
            var response = await next();

            // Inspect Result-based failures (behaviors return Result.Forbidden instead of throwing)
            if (response is Result { IsSuccess: false } failedResult)
            {
                var outcome = failedResult.FailureType == ResultFailureType.Forbidden
                    ? AuditOutcome.Denied
                    : AuditOutcome.Failure;
                await RecordSafelyAsync(
                    entry with { Outcome = outcome, FailureReason = string.Join("; ", failedResult.Errors) },
                    cancellationToken);
            }
            else
            {
                await RecordSafelyAsync(entry, cancellationToken);
            }

            return response;
        }
        catch (ForbiddenAccessException ex)
        {
            await RecordSafelyAsync(
                entry with { Outcome = AuditOutcome.Denied, FailureReason = ex.Message },
                cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            await RecordSafelyAsync(
                entry with { Outcome = AuditOutcome.Failure, FailureReason = ex.GetType().Name },
                cancellationToken);
            throw;
        }
    }

    private async Task RecordSafelyAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _auditSink.RecordAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record audit entry for {RequestType}/{Action}",
                entry.RequestType, entry.Action);
        }
    }
}

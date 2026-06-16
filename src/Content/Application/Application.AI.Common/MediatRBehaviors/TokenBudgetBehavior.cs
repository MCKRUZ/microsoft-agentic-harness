using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.MediatR;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Enforces token budget limits for MediatR requests that consume LLM tokens.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: apply after validation but before any behavior that invokes an LLM.
/// Requests that do not implement <see cref="IConsumesTokens"/> pass through unchanged.
/// </para>
/// <para>
/// Before forwarding a token-consuming request, this behavior calls
/// <see cref="ITokenBudgetTracker.CanAfford"/> against the request's
/// <see cref="IConsumesTokens.EstimatedTokenCost"/>. If the budget cannot be satisfied,
/// a <see cref="TokenBudgetExceededException"/> is thrown and the handler is never called.
/// </para>
/// <para>
/// After the handler returns, this behavior records actual token usage centrally: when the
/// response implements <see cref="IAgentTurnResult"/>, the combined
/// <see cref="IAgentTurnResult.InputTokens"/> + <see cref="IAgentTurnResult.OutputTokens"/>
/// are decremented from the budget via <see cref="ITokenBudgetTracker.RecordUsage"/>. Responses
/// that do not expose token counts leave the budget unchanged.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The MediatR request type.</typeparam>
/// <typeparam name="TResponse">The MediatR response type.</typeparam>
public sealed class TokenBudgetBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly ITokenBudgetTracker _budgetTracker;
    private readonly ILogger<TokenBudgetBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenBudgetBehavior{TRequest,TResponse}"/> class.
    /// </summary>
    public TokenBudgetBehavior(
        ITokenBudgetTracker budgetTracker,
        ILogger<TokenBudgetBehavior<TRequest, TResponse>> logger)
    {
        _budgetTracker = budgetTracker;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IConsumesTokens tokenRequest)
            return await next();

        if (!_budgetTracker.CanAfford(tokenRequest.EstimatedTokenCost))
        {
            _logger.LogWarning(
                "Request {RequestName} rejected: estimated {EstimatedCost:N0} tokens exceeds remaining budget of {Remaining:N0}",
                typeof(TRequest).Name,
                tokenRequest.EstimatedTokenCost,
                _budgetTracker.RemainingBudget);

            throw new TokenBudgetExceededException(_budgetTracker.RemainingBudget, tokenRequest.EstimatedTokenCost);
        }

        _logger.LogDebug(
            "Request {RequestName} approved: {EstimatedCost:N0} tokens estimated (remaining: {Remaining:N0}/{Total:N0})",
            typeof(TRequest).Name,
            tokenRequest.EstimatedTokenCost,
            _budgetTracker.RemainingBudget,
            _budgetTracker.TotalBudget);

        var response = await next();

        // Record actual usage centrally so the scoped budget reflects real consumption
        // rather than the pre-flight estimate. Only turn results expose token counts.
        if (response is IAgentTurnResult turnResult)
        {
            var actualTokens = turnResult.InputTokens + turnResult.OutputTokens;
            if (actualTokens > 0)
                _budgetTracker.RecordUsage(actualTokens);
        }

        return response;
    }
}

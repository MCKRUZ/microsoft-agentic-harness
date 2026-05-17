using Application.AI.Common.Interfaces.Planner;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Delegates step retry to the plan executor.
/// </summary>
public sealed class RetryPlanStepCommandHandler : IRequestHandler<RetryPlanStepCommand, Result>
{
    private readonly IPlanExecutor _executor;
    private readonly ILogger<RetryPlanStepCommandHandler> _logger;

    public RetryPlanStepCommandHandler(
        IPlanExecutor executor,
        ILogger<RetryPlanStepCommandHandler> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<Result> Handle(RetryPlanStepCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Retrying step {StepId} in plan {PlanId}",
            request.StepId.Value, request.PlanId.Value);
        return await _executor.RetryStepAsync(request.PlanId, request.StepId, cancellationToken);
    }
}

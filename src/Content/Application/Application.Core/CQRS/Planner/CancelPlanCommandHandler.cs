using Application.AI.Common.Interfaces.Planner;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Delegates plan cancellation to the plan executor.
/// </summary>
public sealed class CancelPlanCommandHandler : IRequestHandler<CancelPlanCommand, Result>
{
    private readonly IPlanExecutor _executor;
    private readonly ILogger<CancelPlanCommandHandler> _logger;

    public CancelPlanCommandHandler(
        IPlanExecutor executor,
        ILogger<CancelPlanCommandHandler> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<Result> Handle(CancelPlanCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cancelling plan {PlanId}", request.PlanId.Value);
        return await _executor.CancelAsync(request.PlanId, cancellationToken);
    }
}

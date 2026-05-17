using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Loads a plan graph and its current step execution states from the state store.
/// </summary>
public sealed class GetPlanQueryHandler : IRequestHandler<GetPlanQuery, Result<PlanSnapshot>>
{
    private readonly IPlanStateStore _store;
    private readonly ILogger<GetPlanQueryHandler> _logger;

    public GetPlanQueryHandler(
        IPlanStateStore store,
        ILogger<GetPlanQueryHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Result<PlanSnapshot>> Handle(GetPlanQuery request, CancellationToken cancellationToken)
    {
        var loadResult = await _store.LoadPlanAsync(request.PlanId, cancellationToken);
        if (!loadResult.IsSuccess)
            return Result<PlanSnapshot>.Fail(loadResult.Errors.ToArray());

        if (loadResult.Value is null)
            return Result<PlanSnapshot>.NotFound($"Plan '{request.PlanId.Value}' not found.");

        var statesResult = await _store.LoadStepStatesAsync(request.PlanId, cancellationToken);
        if (!statesResult.IsSuccess)
            return Result<PlanSnapshot>.Fail(statesResult.Errors.ToArray());

        var snapshot = new PlanSnapshot
        {
            Graph = loadResult.Value,
            StepStates = statesResult.Value
        };

        return Result<PlanSnapshot>.Success(snapshot);
    }
}

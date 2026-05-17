using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Loads a plan, validates it, and delegates execution to the plan executor.
/// Supports both fresh execution and resumption from checkpoint.
/// </summary>
public sealed class ExecutePlanCommandHandler : IRequestHandler<ExecutePlanCommand, Result<PlanExecutionSummary>>
{
    private readonly IPlanStateStore _store;
    private readonly IPlanValidator _validator;
    private readonly IPlanExecutor _executor;
    private readonly ILogger<ExecutePlanCommandHandler> _logger;

    public ExecutePlanCommandHandler(
        IPlanStateStore store,
        IPlanValidator validator,
        IPlanExecutor executor,
        ILogger<ExecutePlanCommandHandler> logger)
    {
        _store = store;
        _validator = validator;
        _executor = executor;
        _logger = logger;
    }

    public async Task<Result<PlanExecutionSummary>> Handle(ExecutePlanCommand request, CancellationToken cancellationToken)
    {
        var loadResult = await _store.LoadPlanAsync(request.PlanId, cancellationToken);
        if (!loadResult.IsSuccess)
            return Result<PlanExecutionSummary>.Fail(loadResult.Errors.ToArray());

        if (loadResult.Value is null)
            return Result<PlanExecutionSummary>.NotFound($"Plan '{request.PlanId.Value}' not found.");

        var plan = loadResult.Value;

        var validationResult = await _validator.ValidateAsync(plan, cancellationToken);
        if (!validationResult.IsSuccess)
            return Result<PlanExecutionSummary>.Fail(validationResult.Errors.ToArray());

        if (!validationResult.Value.IsValid)
        {
            _logger.LogWarning("Plan {PlanId} failed pre-execution validation: {Errors}",
                request.PlanId.Value, string.Join("; ", validationResult.Value.Errors));
            return Result<PlanExecutionSummary>.ValidationFailure(validationResult.Value.Errors);
        }

        return await _executor.ExecuteAsync(request.PlanId, cancellationToken);
    }
}

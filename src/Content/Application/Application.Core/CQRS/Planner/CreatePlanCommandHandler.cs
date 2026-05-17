using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates a pre-built plan graph and persists it via the state store.
/// </summary>
public sealed class CreatePlanCommandHandler : IRequestHandler<CreatePlanCommand, Result<PlanId>>
{
    private readonly IPlanValidator _validator;
    private readonly IPlanStateStore _store;
    private readonly ILogger<CreatePlanCommandHandler> _logger;

    public CreatePlanCommandHandler(
        IPlanValidator validator,
        IPlanStateStore store,
        ILogger<CreatePlanCommandHandler> logger)
    {
        _validator = validator;
        _store = store;
        _logger = logger;
    }

    public async Task<Result<PlanId>> Handle(CreatePlanCommand request, CancellationToken cancellationToken)
    {
        var validationResult = await _validator.ValidateAsync(request.Plan, cancellationToken);
        if (!validationResult.IsSuccess)
            return Result<PlanId>.Fail(validationResult.Errors.ToArray());

        if (!validationResult.Value.IsValid)
        {
            _logger.LogWarning("Plan validation failed: {Errors}",
                string.Join("; ", validationResult.Value.Errors));
            return Result<PlanId>.ValidationFailure(validationResult.Value.Errors);
        }

        var saveResult = await _store.SavePlanAsync(request.Plan, cancellationToken);
        if (!saveResult.IsSuccess)
            return Result<PlanId>.Fail(saveResult.Errors.ToArray());

        return Result<PlanId>.Success(request.Plan.Id);
    }
}

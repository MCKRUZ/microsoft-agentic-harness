using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Generates a plan via LLM, validates the resulting graph, and persists it.
/// </summary>
public sealed class GeneratePlanCommandHandler : IRequestHandler<GeneratePlanCommand, Result<PlanId>>
{
    private readonly IPlanGenerator _generator;
    private readonly IPlanValidator _validator;
    private readonly IPlanStateStore _store;
    private readonly ILogger<GeneratePlanCommandHandler> _logger;

    public GeneratePlanCommandHandler(
        IPlanGenerator generator,
        IPlanValidator validator,
        IPlanStateStore store,
        ILogger<GeneratePlanCommandHandler> logger)
    {
        _generator = generator;
        _validator = validator;
        _store = store;
        _logger = logger;
    }

    public async Task<Result<PlanId>> Handle(GeneratePlanCommand request, CancellationToken cancellationToken)
    {
        var generationResult = await _generator.GenerateAsync(
            request.TaskDescription, request.Constraints, cancellationToken);

        if (!generationResult.IsSuccess)
            return Result<PlanId>.Fail(generationResult.Errors.ToArray());

        var plan = generationResult.Value;

        var validationResult = await _validator.ValidateAsync(plan, cancellationToken);
        if (!validationResult.IsSuccess)
            return Result<PlanId>.Fail(validationResult.Errors.ToArray());

        if (!validationResult.Value.IsValid)
        {
            _logger.LogWarning("Generated plan failed validation: {Errors}",
                string.Join("; ", validationResult.Value.Errors));
            return Result<PlanId>.ValidationFailure(validationResult.Value.Errors);
        }

        var saveResult = await _store.SavePlanAsync(plan, cancellationToken);
        if (!saveResult.IsSuccess)
            return Result<PlanId>.Fail(saveResult.Errors.ToArray());

        return Result<PlanId>.Success(plan.Id);
    }
}

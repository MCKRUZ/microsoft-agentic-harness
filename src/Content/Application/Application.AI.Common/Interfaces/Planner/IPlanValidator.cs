using Domain.AI.Planner;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Validates a <see cref="PlanGraph"/> before execution. Performs structural checks
/// (cycle detection, unreachable nodes, referential integrity) and semantic checks
/// (step configuration validity, conditional branch completeness).
/// </summary>
public interface IPlanValidator
{
    /// <summary>
    /// Validates the plan graph and returns errors, warnings, and estimated critical path duration.
    /// </summary>
    /// <param name="plan">The plan graph to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result on success, or a failure result for infrastructure errors.</returns>
    Task<Result<PlanValidationResult>> ValidateAsync(PlanGraph plan, CancellationToken ct);
}

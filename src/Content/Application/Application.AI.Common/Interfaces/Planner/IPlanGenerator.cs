using Domain.AI.Planner;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Generates a <see cref="PlanGraph"/> from a natural-language task description using
/// LLM inference. The generated plan is validated before being returned.
/// </summary>
public interface IPlanGenerator
{
    /// <summary>
    /// Generates a structured plan from the task description, optionally bounded by constraints.
    /// </summary>
    /// <param name="taskDescription">Natural-language description of the task to plan.</param>
    /// <param name="constraints">Optional constraints on plan complexity and structure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A validated plan graph, or a failure result if generation or validation fails.</returns>
    Task<Result<PlanGraph>> GenerateAsync(
        string taskDescription,
        PlanGenerationConstraints? constraints = null,
        CancellationToken ct = default);
}

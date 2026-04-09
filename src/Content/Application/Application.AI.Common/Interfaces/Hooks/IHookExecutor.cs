using Domain.AI.Hooks;

namespace Application.AI.Common.Interfaces.Hooks;

/// <summary>
/// Executes matching hooks for a given event. Handles parallel execution,
/// timeouts, error isolation, and result aggregation.
/// </summary>
public interface IHookExecutor
{
    /// <summary>
    /// Executes all hooks matching the event and context.
    /// Returns aggregated results from all executed hooks.
    /// </summary>
    /// <param name="hookEvent">The lifecycle event that triggered hook execution.</param>
    /// <param name="context">Contextual information about the triggering event.</param>
    /// <param name="cancellationToken">Cancellation token for the overall operation.</param>
    /// <returns>Results from all executed hooks, in execution order.</returns>
    Task<IReadOnlyList<HookResult>> ExecuteHooksAsync(
        HookEvent hookEvent,
        HookExecutionContext context,
        CancellationToken cancellationToken = default);
}

using Domain.AI.Planner;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Optional per-step retry settings a caller may request when submitting a workflow.
/// </summary>
/// <remarks>
/// <para>
/// Retries multiply a step's cost, so <see cref="MaxRetries"/> is bounded by the host's configured
/// ceiling and a request above it is rejected rather than clamped.
/// </para>
/// <para>
/// <strong>These settings never apply to a governance denial.</strong> A step refused by the
/// permission system fails terminally and is not retried, whatever is requested here — a denial is an
/// answered question, and retrying it would only ask again. The same is true of an autonomy-ceiling
/// violation. This is enforced centrally in the executor, not per step type, so it cannot be
/// configured away from the wire.
/// </para>
/// </remarks>
public sealed record WorkflowRetrySettings
{
    /// <summary>
    /// Requested number of retry attempts after the first failure. Rejected if above the host's
    /// ceiling. Zero means the first failure is terminal.
    /// </summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Requested delay before the first retry. Subsequent delays follow <see cref="Strategy"/>.
    /// </summary>
    public TimeSpan? InitialDelay { get; init; }

    /// <summary>How the delay grows between attempts. When omitted, the host default applies.</summary>
    public BackoffStrategy? Strategy { get; init; }

    /// <summary>
    /// What happens when the retry budget is exhausted — fail the step, skip it, escalate, or fail the
    /// whole plan. When omitted, the host default applies.
    /// </summary>
    public ErrorRecovery? OnExhausted { get; init; }
}

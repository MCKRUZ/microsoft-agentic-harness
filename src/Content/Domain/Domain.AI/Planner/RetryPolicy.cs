namespace Domain.AI.Planner;

/// <summary>
/// Configures retry behavior for a plan step, including backoff strategy
/// and the action to take when all retries are exhausted.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>
    /// Maximum number of automatic retries after the initial attempt before invoking
    /// <see cref="OnExhausted"/> — a step is executed at most 1 + MaxRetries times. Zero disables
    /// automatic retry. Failed results, per-attempt timeouts, and unhandled executor exceptions
    /// all consume this budget; operator-initiated retries do not.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Delay before the first retry. Subsequent delays depend on <see cref="Strategy"/>.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How retry delays scale between attempts.</summary>
    public BackoffStrategy Strategy { get; init; } = BackoffStrategy.Exponential;

    /// <summary>Action taken when all retry attempts are exhausted.</summary>
    public ErrorRecovery OnExhausted { get; init; } = ErrorRecovery.FailStep;
}

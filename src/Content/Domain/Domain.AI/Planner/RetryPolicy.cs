namespace Domain.AI.Planner;

/// <summary>
/// Configures retry behavior for a plan step, including backoff strategy
/// and the action to take when all retries are exhausted.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Maximum number of retry attempts before invoking <see cref="OnExhausted"/>.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Delay before the first retry. Subsequent delays depend on <see cref="Strategy"/>.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How retry delays scale between attempts.</summary>
    public BackoffStrategy Strategy { get; init; } = BackoffStrategy.Exponential;

    /// <summary>Action taken when all retry attempts are exhausted.</summary>
    public ErrorRecovery OnExhausted { get; init; } = ErrorRecovery.FailStep;
}

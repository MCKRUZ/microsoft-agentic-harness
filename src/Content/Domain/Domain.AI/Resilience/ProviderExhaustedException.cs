namespace Domain.AI.Resilience;

/// <summary>
/// Thrown when every provider in the fallback chain has failed.
/// Carries structured failure information for callers to decide on retry, degraded response, or escalation.
/// </summary>
public sealed class ProviderExhaustedException : Exception
{
    /// <summary>Which providers were attempted before exhaustion.</summary>
    public IReadOnlyList<string> FailedProviders { get; }

    /// <summary>Suggested wait time before retrying, derived from the shortest circuit breaker break duration.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>Creates a new instance with the specified failed providers and retry hint.</summary>
    public ProviderExhaustedException(IReadOnlyList<string> failedProviders, TimeSpan retryAfter)
        : base($"All LLM providers exhausted: {string.Join(", ", failedProviders)}. Retry after {retryAfter.TotalSeconds}s.")
    {
        FailedProviders = failedProviders.ToArray();
        RetryAfter = retryAfter;
    }

    /// <summary>Creates a new instance wrapping the last provider's exception.</summary>
    public ProviderExhaustedException(IReadOnlyList<string> failedProviders, TimeSpan retryAfter, Exception innerException)
        : base($"All LLM providers exhausted: {string.Join(", ", failedProviders)}. Retry after {retryAfter.TotalSeconds}s.", innerException)
    {
        FailedProviders = failedProviders.ToArray();
        RetryAfter = retryAfter;
    }
}

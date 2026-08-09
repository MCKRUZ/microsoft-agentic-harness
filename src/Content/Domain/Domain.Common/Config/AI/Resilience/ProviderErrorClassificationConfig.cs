namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// Consumer-tunable inputs to provider failure classification.
/// Bound from <c>AppConfig:AI:Resilience:ErrorClassification</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only the message-pattern extension points are configurable. HTTP status mapping is
/// protocol-level, identical across providers, and deliberately kept in code — exposing it
/// as configuration would invite a consumer to make a 503 non-retryable with no compiler or
/// test to catch it.
/// </para>
/// <para>
/// These lists <b>add to</b> the built-in defaults rather than replacing them, for two reasons.
/// A consumer adding one provider's wording should not silently lose the coverage for every
/// other provider; and .NET configuration binds arrays positionally without clearing the
/// target, so a "replace" semantic would leave defaults dangling past the end of a shorter
/// configured array.
/// </para>
/// </remarks>
public class ProviderErrorClassificationConfig
{
    /// <summary>
    /// Extra case-insensitive substrings that mark a failure as fatal for the whole chain —
    /// a credential, billing, or account-state cause that rotating providers cannot fix.
    /// </summary>
    /// <remarks>
    /// Consulted only when the failure's HTTP status is already a client error (4xx) or is
    /// absent entirely. A status that positively indicates a transient failure (429, 5xx)
    /// is never overridden by a message match, so a pattern as broad as <c>"expired"</c>
    /// cannot turn a genuine rate limit into a fatal error.
    /// </remarks>
    public string[] AdditionalChainFatalMessagePatterns { get; set; } = [];

    /// <summary>
    /// Extra case-insensitive substrings that mark a failure as fatal for the current provider
    /// but still eligible for fallback — for example a model this provider does not host.
    /// </summary>
    /// <remarks>Subject to the same 4xx-or-absent-status precondition as
    /// <see cref="AdditionalChainFatalMessagePatterns"/>.</remarks>
    public string[] AdditionalProviderFatalMessagePatterns { get; set; } = [];
}

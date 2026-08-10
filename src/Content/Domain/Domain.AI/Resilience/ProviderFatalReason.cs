namespace Domain.AI.Resilience;

/// <summary>
/// Stable, scrubbed identifiers naming why a provider failure was classified as fatal.
/// </summary>
/// <remarks>
/// <para>
/// These codes are the operator-visible and caller-visible name for the cause. They are
/// deliberately stable strings rather than an enum so downstream consumers can match on them
/// across template versions, and deliberately scrubbed so nothing derived from the provider's
/// own error text ever reaches a caller — provider messages have been observed to carry SAS
/// tokens and key fragments. The verbatim provider message is written to the structured log
/// at the point of classification instead.
/// </para>
/// </remarks>
public static class ProviderFatalReason
{
    /// <summary>The provider rejected the supplied credential. Prefix shared by all fatal codes.</summary>
    public const string InvalidCredentials = "provider.fatal.invalid_credentials";

    /// <summary>The account has no remaining balance, credits, or an inactive billing state.</summary>
    public const string BillingExhausted = "provider.fatal.billing_exhausted";

    /// <summary>The credential is valid but is not permitted to perform this operation.</summary>
    public const string AccessDenied = "provider.fatal.access_denied";

    /// <summary>The requested model or deployment does not exist on this provider.</summary>
    public const string ModelNotFound = "provider.fatal.model_not_found";

    /// <summary>The provider rejected the request itself as malformed or unsupported.</summary>
    public const string RequestRejected = "provider.fatal.request_rejected";

    /// <summary>
    /// A non-retryable cause matched a consumer-supplied pattern, which names the wording but
    /// not the category. Used when the harness cannot attribute the failure more precisely.
    /// </summary>
    public const string Configuration = "provider.fatal.configuration";

    /// <summary>
    /// The harness itself refused the call because this provider's circuit breaker is open.
    /// Not a provider response at all — the request never left the process.
    /// </summary>
    public const string CircuitOpen = "provider.fatal.circuit_open";
}

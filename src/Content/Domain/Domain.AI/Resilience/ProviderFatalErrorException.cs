namespace Domain.AI.Resilience;

/// <summary>
/// Thrown when a provider failure is one that no amount of retrying and no other provider in
/// the chain can resolve — a rejected credential, an exhausted balance, a disabled account.
/// </summary>
/// <remarks>
/// <para>
/// This exception exists so the real cause survives the resilience layers intact. Without it,
/// a rejected API key is retried, trips the circuit breaker, rotates the fallback chain, and
/// finally reaches the operator as <see cref="ProviderExhaustedException"/> — "all providers
/// exhausted", which names a symptom the operator cannot act on and hides the one-line
/// configuration fix that would resolve it.
/// </para>
/// <para>
/// The message carries the stable <see cref="ReasonCode"/> and the provider name only. The
/// provider's own error text is deliberately absent: it is written to the structured log at
/// the point of classification, because provider messages have been observed to echo back
/// credential fragments and SAS tokens, and this exception's message may reach an API response.
/// </para>
/// </remarks>
public sealed class ProviderFatalErrorException : Exception
{
    /// <summary>The provider whose failure was classified as fatal.</summary>
    public string ProviderName { get; }

    /// <summary>A stable <see cref="ProviderFatalReason"/> constant naming the cause.</summary>
    public string ReasonCode { get; }

    /// <summary>
    /// Providers that failed earlier in the chain before this one stopped it, in the order they
    /// were tried. Empty when the chain stopped on its first provider.
    /// </summary>
    /// <remarks>
    /// Without this, a chain that is rate-limited on its primary and then rejected on its
    /// fallback reports only the fallback, and the primary's failure never reaches the caller —
    /// the operator sees a credential problem on a provider they were not even using by choice.
    /// </remarks>
    public IReadOnlyList<string> FailedProviders { get; }

    /// <summary>Creates a new instance for the given provider and reason.</summary>
    /// <param name="providerName">The provider whose failure was classified as fatal.</param>
    /// <param name="reasonCode">A <see cref="ProviderFatalReason"/> constant.</param>
    /// <param name="innerException">The original provider exception, preserved for logging.</param>
    /// <param name="failedProviders">Providers that failed earlier in the chain, if any.</param>
    public ProviderFatalErrorException(
        string providerName,
        string reasonCode,
        Exception innerException,
        IReadOnlyList<string>? failedProviders = null)
        : base(BuildMessage(providerName, reasonCode, failedProviders), innerException)
    {
        ProviderName = providerName;
        ReasonCode = reasonCode;
        FailedProviders = failedProviders?.ToArray() ?? [];
    }

    private static string BuildMessage(
        string providerName, string reasonCode, IReadOnlyList<string>? failedProviders)
    {
        var earlier = failedProviders is { Count: > 0 }
            ? $" Providers tried earlier and failed: {string.Join(", ", failedProviders)}."
            : string.Empty;

        return $"Provider '{providerName}' failed with a non-retryable error ({reasonCode}). " +
               "Retrying and provider fallback were skipped because neither can resolve this cause." +
               earlier +
               " See the structured log for the provider's original message.";
    }
}

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

    /// <summary>Creates a new instance for the given provider and reason.</summary>
    /// <param name="providerName">The provider whose failure was classified as fatal.</param>
    /// <param name="reasonCode">A <see cref="ProviderFatalReason"/> constant.</param>
    /// <param name="innerException">The original provider exception, preserved for logging.</param>
    public ProviderFatalErrorException(string providerName, string reasonCode, Exception innerException)
        : base(BuildMessage(providerName, reasonCode), innerException)
    {
        ProviderName = providerName;
        ReasonCode = reasonCode;
    }

    private static string BuildMessage(string providerName, string reasonCode)
        => $"Provider '{providerName}' failed with a non-retryable error ({reasonCode}). " +
           "Retrying and provider fallback were skipped because neither can resolve this cause. " +
           "See the structured log for the provider's original message.";
}

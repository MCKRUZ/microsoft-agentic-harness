using Domain.AI.Resilience;

namespace Application.AI.Common.Interfaces.Resilience;

/// <summary>
/// Decides what the resilience pipeline should do about a provider failure: retry it,
/// count it against the provider's health, fall back to the next provider, or stop.
/// </summary>
/// <remarks>
/// <para>
/// This is the single seam through which retry, circuit-breaker accounting, and cross-provider
/// fallback all make their decision. Before it existed those three decisions were made from a
/// hard-coded list of .NET exception types, which meant they only ever covered whichever
/// provider happened to throw a matching type — Anthropic, in practice, while the Azure
/// providers in the shipped fallback chain passed straight through unhandled.
/// </para>
/// <para>
/// Implementations must be thread-safe and must not perform I/O: this runs inside Polly's
/// <c>ShouldHandle</c> predicate on every failed attempt.
/// </para>
/// <para>
/// Consumers adding a provider whose failure wording is not covered should prefer extending
/// <see cref="Domain.Common.Config.AI.Resilience.ProviderErrorClassificationConfig"/> over
/// replacing this service; replace it only when the classification needs to consider
/// something other than HTTP status and message text.
/// </para>
/// </remarks>
public interface IProviderErrorClassifier
{
    /// <summary>
    /// Classifies a provider failure.
    /// </summary>
    /// <param name="exception">The exception thrown by the provider call.</param>
    /// <returns>
    /// The classification. Implementations must return
    /// <see cref="ProviderFailureKind.Unknown"/> rather than guessing when the failure is not
    /// positively recognised — an unrecognised failure is not retried, but is still counted
    /// against the provider's health.
    /// </returns>
    ProviderFailureClassification Classify(Exception exception);
}

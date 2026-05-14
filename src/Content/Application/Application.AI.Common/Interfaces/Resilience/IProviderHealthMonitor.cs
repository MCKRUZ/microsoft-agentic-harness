using Domain.AI.Resilience;

namespace Application.AI.Common.Interfaces.Resilience;

/// <summary>
/// Exposes circuit breaker health state for configured LLM providers. Maps
/// Polly <c>CircuitState</c> to <see cref="ProviderHealthState"/>: Closed = Healthy,
/// HalfOpen = Degraded, Open/Isolated = Unavailable.
/// </summary>
/// <remarks>
/// <para>
/// No synthetic pre-warm probes are used. LLM API calls cost tokens and there is
/// no lightweight health endpoint. Recovery detection relies on Polly's built-in
/// half-open behavior: when a circuit transitions to HalfOpen, the next real request
/// serves as the recovery probe.
/// </para>
/// <para>
/// The <see cref="OnCircuitStateChanged"/> event fires on every state transition,
/// enabling OTel gauge updates and retry queue drain triggers without polling.
/// </para>
/// </remarks>
public interface IProviderHealthMonitor
{
	/// <summary>
	/// Gets the current health state for a specific provider.
	/// Returns <see cref="ProviderHealthState.Healthy"/> if the provider is unknown.
	/// </summary>
	/// <param name="providerName">The provider identifier (e.g., "azure-openai", "anthropic").</param>
	/// <returns>The current <see cref="ProviderHealthState"/> for the provider.</returns>
	ProviderHealthState GetProviderHealth(string providerName);

	/// <summary>
	/// Gets the current health state for all configured providers.
	/// </summary>
	/// <returns>
	/// A read-only dictionary mapping provider names to their current
	/// <see cref="ProviderHealthState"/>.
	/// </returns>
	IReadOnlyDictionary<string, ProviderHealthState> GetAllProviderHealth();

	/// <summary>
	/// Returns true if at least one provider is in the <see cref="ProviderHealthState.Healthy"/> state.
	/// Used by the retry queue to determine if drain is possible.
	/// </summary>
	bool IsAnyProviderHealthy();

	/// <summary>
	/// Raised when any provider's circuit breaker changes state. The callback receives
	/// the provider name and the new <see cref="ProviderHealthState"/>.
	/// </summary>
	event Action<string, ProviderHealthState>? OnCircuitStateChanged;
}

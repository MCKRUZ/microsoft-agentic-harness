diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Resilience/IProviderHealthMonitor.cs b/src/Content/Application/Application.AI.Common/Interfaces/Resilience/IProviderHealthMonitor.cs
new file mode 100644
index 0000000..67d98d8
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Resilience/IProviderHealthMonitor.cs
@@ -0,0 +1,52 @@
+using Domain.AI.Resilience;
+
+namespace Application.AI.Common.Interfaces.Resilience;
+
+/// <summary>
+/// Exposes circuit breaker health state for configured LLM providers. Maps
+/// Polly <c>CircuitState</c> to <see cref="ProviderHealthState"/>: Closed = Healthy,
+/// HalfOpen = Degraded, Open/Isolated = Unavailable.
+/// </summary>
+/// <remarks>
+/// <para>
+/// No synthetic pre-warm probes are used. LLM API calls cost tokens and there is
+/// no lightweight health endpoint. Recovery detection relies on Polly's built-in
+/// half-open behavior: when a circuit transitions to HalfOpen, the next real request
+/// serves as the recovery probe.
+/// </para>
+/// <para>
+/// The <see cref="OnCircuitStateChanged"/> event fires on every state transition,
+/// enabling OTel gauge updates and retry queue drain triggers without polling.
+/// </para>
+/// </remarks>
+public interface IProviderHealthMonitor
+{
+	/// <summary>
+	/// Gets the current health state for a specific provider.
+	/// Returns <see cref="ProviderHealthState.Healthy"/> if the provider is unknown.
+	/// </summary>
+	/// <param name="providerName">The provider identifier (e.g., "azure-openai", "anthropic").</param>
+	/// <returns>The current <see cref="ProviderHealthState"/> for the provider.</returns>
+	ProviderHealthState GetProviderHealth(string providerName);
+
+	/// <summary>
+	/// Gets the current health state for all configured providers.
+	/// </summary>
+	/// <returns>
+	/// A read-only dictionary mapping provider names to their current
+	/// <see cref="ProviderHealthState"/>.
+	/// </returns>
+	IReadOnlyDictionary<string, ProviderHealthState> GetAllProviderHealth();
+
+	/// <summary>
+	/// Returns true if at least one provider is in the <see cref="ProviderHealthState.Healthy"/> state.
+	/// Used by the retry queue to determine if drain is possible.
+	/// </summary>
+	bool IsAnyProviderHealthy();
+
+	/// <summary>
+	/// Raised when any provider's circuit breaker changes state. The callback receives
+	/// the provider name and the new <see cref="ProviderHealthState"/>.
+	/// </summary>
+	event Action<string, ProviderHealthState>? OnCircuitStateChanged;
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Resilience/IResilientChatClientProvider.cs b/src/Content/Application/Application.AI.Common/Interfaces/Resilience/IResilientChatClientProvider.cs
new file mode 100644
index 0000000..3d00457
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Resilience/IResilientChatClientProvider.cs
@@ -0,0 +1,36 @@
+using Microsoft.Extensions.AI;
+
+namespace Application.AI.Common.Interfaces.Resilience;
+
+/// <summary>
+/// Provides a pre-composed <see cref="IChatClient"/> that wraps the configured
+/// provider fallback chain with per-provider resilience pipelines (retry, circuit
+/// breaker, timeout). The returned client is transparent to consumers -- it
+/// implements <see cref="IChatClient"/> and attaches <see cref="Domain.AI.Resilience.FallbackMetadata"/>
+/// to responses when fallback occurs.
+/// </summary>
+/// <remarks>
+/// <para>
+/// This is intentionally separate from <see cref="IChatClientFactory"/>. The factory
+/// contract is "give me a client for a specific provider + deployment." This contract
+/// is "give me a single client that spans all configured providers with automatic
+/// fallback and resilience." These are fundamentally different operations.
+/// </para>
+/// <para>
+/// When <c>ResilienceConfig.Enabled</c> is false, the implementation returns the
+/// primary provider's raw client directly (no Polly wrapping, no fallback chain).
+/// </para>
+/// </remarks>
+public interface IResilientChatClientProvider
+{
+	/// <summary>
+	/// Returns a resilient chat client wrapping the full provider fallback chain.
+	/// The result is cached -- the provider chain does not change at runtime.
+	/// </summary>
+	/// <param name="ct">Cancellation token.</param>
+	/// <returns>
+	/// An <see cref="IChatClient"/> that transparently handles provider failover
+	/// and per-provider resilience policies.
+	/// </returns>
+	Task<IChatClient> GetResilientChatClientAsync(CancellationToken ct = default);
+}

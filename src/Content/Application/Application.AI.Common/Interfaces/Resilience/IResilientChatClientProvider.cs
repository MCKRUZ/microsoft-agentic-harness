using Domain.AI.Resilience;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Resilience;

/// <summary>
/// Provides a pre-composed <see cref="IChatClient"/> that wraps the configured
/// provider fallback chain with per-provider resilience pipelines (retry, circuit
/// breaker, timeout). The returned client is transparent to consumers -- it
/// implements <see cref="IChatClient"/> and attaches <see cref="FallbackMetadata"/>
/// to responses when fallback occurs.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally separate from <see cref="IChatClientFactory"/>. The factory
/// contract is "give me a client for a specific provider + deployment." This contract
/// is "give me a single client that spans all configured providers with automatic
/// fallback and resilience." These are fundamentally different operations.
/// </para>
/// <para>
/// When <c>ResilienceConfig.Enabled</c> is false, the implementation returns the
/// primary provider's raw client directly (no Polly wrapping, no fallback chain).
/// </para>
/// </remarks>
public interface IResilientChatClientProvider
{
	/// <summary>
	/// Key under which <c>AgentExecutionContextFactory</c> stashes the composed resilient
	/// chat client in <c>AgentExecutionContext.AdditionalProperties</c> when
	/// <c>ResilienceConfig.Enabled</c> is true. <c>AgentFactory</c> consumes this key when
	/// building the agent's chat-client pipeline so that live turns execute through the
	/// per-provider Polly pipelines and the fallback chain instead of the raw provider client.
	/// </summary>
	const string AdditionalPropertiesKey = "__resilientChatClient";

	/// <summary>
	/// Returns a resilient chat client wrapping the full provider fallback chain.
	/// The result is cached -- the provider chain does not change at runtime.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>
	/// An <see cref="IChatClient"/> that transparently handles provider failover
	/// and per-provider resilience policies.
	/// </returns>
	Task<IChatClient> GetResilientChatClientAsync(CancellationToken ct = default);
}

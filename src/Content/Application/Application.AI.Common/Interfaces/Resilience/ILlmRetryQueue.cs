using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Resilience;

/// <summary>
/// Accepts failed LLM requests for automatic retry when a provider recovers.
/// The implementation is a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// that monitors circuit breaker state and retries queued requests when a provider
/// transitions to healthy.
/// </summary>
/// <remarks>
/// Conditionally registered only when <c>ResilienceConfig.Enabled == true</c>.
/// The queue is bounded by <c>DegradedModeConfig.MaxQueueSize</c> with TTL enforcement
/// via <c>DegradedModeConfig.RetryQueueTtlSeconds</c>.
/// </remarks>
public interface ILlmRetryQueue
{
    /// <summary>
    /// Enqueues a failed LLM request for automatic retry when a provider recovers.
    /// Returns a Task that completes when the request is eventually retried successfully,
    /// expires (TTL), or is evicted (queue full).
    /// </summary>
    /// <param name="messages">The original chat messages.</param>
    /// <param name="options">The original chat options.</param>
    /// <param name="callerCancellation">The original caller's cancellation token.</param>
    /// <returns>
    /// A Task that completes with the <see cref="ChatResponse"/> on successful retry,
    /// faults with <see cref="Domain.AI.Resilience.ProviderExhaustedException"/> on TTL expiry or eviction,
    /// or cancels if the caller's token fires.
    /// </returns>
    Task<ChatResponse> EnqueueAsync(
        IList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken callerCancellation);
}

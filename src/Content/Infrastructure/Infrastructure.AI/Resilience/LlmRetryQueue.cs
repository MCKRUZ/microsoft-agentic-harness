using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Resilience;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Resilience;
using Domain.Common.Config.AI.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Resilience;

/// <summary>
/// Represents a queued LLM request awaiting retry after all providers were exhausted.
/// </summary>
internal sealed record QueuedLlmRequest
{
    /// <summary>The original chat messages to retry.</summary>
    public required IList<ChatMessage> Messages { get; init; }

    /// <summary>The original chat options (model, temperature, etc.).</summary>
    public ChatOptions? Options { get; init; }

    /// <summary>
    /// Completion source that callers await. Completed with the response on successful
    /// retry, or with <see cref="ProviderExhaustedException"/> on TTL expiry.
    /// </summary>
    public required TaskCompletionSource<ChatResponse> CompletionSource { get; init; }

    /// <summary>When this request was enqueued. Used for TTL enforcement.</summary>
    public required DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>Absolute expiry time (EnqueuedAt + TTL).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The original caller's cancellation token. Checked before retry to avoid
    /// wasting LLM tokens on abandoned requests.
    /// </summary>
    public required CancellationToken CallerCancellation { get; init; }
}

/// <summary>
/// In-memory retry queue for LLM requests that failed due to all providers being
/// exhausted. Monitors circuit breaker recovery via <see cref="IProviderHealthMonitor"/>
/// and automatically retries queued requests when a provider becomes healthy.
/// </summary>
/// <remarks>
/// <para>
/// Conditionally registered as <see cref="IHostedService"/> only when
/// <c>ResilienceConfig.Enabled == true</c>. When resilience is disabled, this
/// service is not in the DI container at all.
/// </para>
/// <para>
/// The queue is bounded by <see cref="DegradedModeConfig.MaxQueueSize"/>. When full,
/// the oldest request is evicted and its <see cref="TaskCompletionSource{T}"/> is
/// completed with <see cref="ProviderExhaustedException"/>.
/// </para>
/// <para>
/// TTL enforcement runs on a periodic sweep (every 10 seconds). Expired items have
/// their TCS completed with <see cref="ProviderExhaustedException"/>.
/// </para>
/// </remarks>
public sealed class LlmRetryQueue : BackgroundService, ILlmRetryQueue
{
    private readonly IProviderHealthMonitor _healthMonitor;
    private readonly IResilientChatClientProvider _chatClientProvider;
    private readonly IOptionsMonitor<ResilienceConfig> _resilienceConfig;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlmRetryQueue> _logger;
    private readonly ConcurrentQueue<QueuedLlmRequest> _queue = new();
    private readonly SemaphoreSlim _drainSignal = new(0, 1);
    private readonly object _enqueueLock = new();
    private int _queueDepth;
    private IChatClient? _cachedClient;

    /// <summary>Creates a new retry queue instance.</summary>
    /// <param name="healthMonitor">Health monitor for circuit breaker state queries and recovery events.</param>
    /// <param name="chatClientProvider">Provider for the resilient chat client used to retry requests.</param>
    /// <param name="resilienceConfig">Configuration for queue size and TTL.</param>
    /// <param name="timeProvider">Time provider for TTL calculations (testable via FakeTimeProvider).</param>
    /// <param name="logger">Logger for queue operations.</param>
    public LlmRetryQueue(
        IProviderHealthMonitor healthMonitor,
        IResilientChatClientProvider chatClientProvider,
        IOptionsMonitor<ResilienceConfig> resilienceConfig,
        TimeProvider timeProvider,
        ILogger<LlmRetryQueue> logger)
    {
        _healthMonitor = healthMonitor;
        _chatClientProvider = chatClientProvider;
        _resilienceConfig = resilienceConfig;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Current queue depth. Tracked via Interlocked for O(1) reads.</summary>
    internal int QueueDepth => _queueDepth;

    /// <summary>
    /// Enqueues a failed LLM request for automatic retry when a provider recovers.
    /// Returns a Task that completes when the request is eventually retried successfully
    /// or expires.
    /// </summary>
    /// <param name="messages">The original chat messages.</param>
    /// <param name="options">The original chat options.</param>
    /// <param name="callerCancellation">The original caller's cancellation token.</param>
    /// <returns>A Task that completes with the ChatResponse on successful retry.</returns>
    public Task<ChatResponse> EnqueueAsync(
        IList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken callerCancellation)
    {
        var config = _resilienceConfig.CurrentValue.DegradedMode;
        var now = _timeProvider.GetUtcNow();
        var tcs = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var request = new QueuedLlmRequest
        {
            Messages = messages,
            Options = options,
            CompletionSource = tcs,
            EnqueuedAt = now,
            ExpiresAt = now.AddSeconds(config.RetryQueueTtlSeconds),
            CallerCancellation = callerCancellation
        };

        lock (_enqueueLock)
        {
            _queue.Enqueue(request);
            var depth = Interlocked.Increment(ref _queueDepth);
            ResilienceMetrics.QueueSize.Add(1);

            while (depth > config.MaxQueueSize && _queue.TryDequeue(out var evicted))
            {
                Interlocked.Decrement(ref _queueDepth);
                ResilienceMetrics.QueueSize.Add(-1);
                ResilienceMetrics.QueueExpired.Add(1);
                evicted.CompletionSource.TrySetException(
                    new ProviderExhaustedException(Array.Empty<string>(), TimeSpan.Zero));
                depth = _queueDepth;
            }
        }

        return tcs.Task;
    }

    /// <summary>
    /// Removes TTL-expired items from the queue, completing their TCS with
    /// <see cref="ProviderExhaustedException"/>. Non-expired items are re-enqueued.
    /// </summary>
    internal void SweepExpired()
    {
        var now = _timeProvider.GetUtcNow();
        var count = _queueDepth;

        for (var i = 0; i < count; i++)
        {
            if (!_queue.TryDequeue(out var item))
                break;

            if (item.ExpiresAt <= now)
            {
                Interlocked.Decrement(ref _queueDepth);
                ResilienceMetrics.QueueSize.Add(-1);
                ResilienceMetrics.QueueExpired.Add(1);
                item.CompletionSource.TrySetException(
                    new ProviderExhaustedException(Array.Empty<string>(), TimeSpan.Zero));
            }
            else
            {
                _queue.Enqueue(item);
            }
        }
    }

    /// <summary>
    /// Attempts to retry all queued requests using the resilient chat client.
    /// Skips cancelled requests, re-enqueues on provider exhaustion, and removes
    /// successfully completed or failed items.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the drain operation.</param>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        if (!_healthMonitor.IsAnyProviderHealthy())
            return;

        // Safe to cache: IResilientChatClientProvider caches internally and the provider chain is immutable at runtime.
        _cachedClient ??= await _chatClientProvider.GetResilientChatClientAsync(cancellationToken);

        var count = _queueDepth;
        for (var i = 0; i < count; i++)
        {
            if (!_queue.TryDequeue(out var item))
                break;

            if (item.CallerCancellation.IsCancellationRequested)
            {
                Interlocked.Decrement(ref _queueDepth);
                ResilienceMetrics.QueueSize.Add(-1);
                item.CompletionSource.TrySetCanceled(item.CallerCancellation);
                continue;
            }

            if (item.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                Interlocked.Decrement(ref _queueDepth);
                ResilienceMetrics.QueueSize.Add(-1);
                ResilienceMetrics.QueueExpired.Add(1);
                item.CompletionSource.TrySetException(
                    new ProviderExhaustedException(Array.Empty<string>(), TimeSpan.Zero));
                continue;
            }

            try
            {
                var response = await _cachedClient.GetResponseAsync(
                    item.Messages, item.Options, item.CallerCancellation);

                Interlocked.Decrement(ref _queueDepth);
                ResilienceMetrics.QueueSize.Add(-1);
                item.CompletionSource.TrySetResult(response);
                _logger.LogDebug("Retry queue drained request successfully");
            }
            catch (OperationCanceledException) when (item.CallerCancellation.IsCancellationRequested)
            {
                Interlocked.Decrement(ref _queueDepth);
                ResilienceMetrics.QueueSize.Add(-1);
                item.CompletionSource.TrySetCanceled(item.CallerCancellation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _queue.Enqueue(item);
                break;
            }
            catch (ProviderExhaustedException)
            {
                _queue.Enqueue(item);
                _logger.LogWarning("Retry failed during drain — providers exhausted again, re-enqueued");
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _queueDepth);
                ResilienceMetrics.QueueSize.Add(-1);
                item.CompletionSource.TrySetException(ex);
                _logger.LogWarning(ex, "Retry queue request failed with unexpected exception");
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnStateChanged(string providerName, ProviderHealthState newState)
        {
            if (newState == ProviderHealthState.Healthy)
            {
                try { _drainSignal.Release(); }
                catch (SemaphoreFullException) { /* coalesce — already signaled */ }
            }
        }

        _healthMonitor.OnCircuitStateChanged += OnStateChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _drainSignal.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken)
                    .ConfigureAwait(false);

                SweepExpired();

                if (_healthMonitor.IsAnyProviderHealthy())
                    await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        finally
        {
            _healthMonitor.OnCircuitStateChanged -= OnStateChanged;

            var abandoned = 0;
            while (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _queueDepth);
                item.CompletionSource.TrySetCanceled(CancellationToken.None);
                abandoned++;
            }

            if (abandoned > 0)
                _logger.LogWarning("LlmRetryQueue shutting down, abandoned {Count} queued requests", abandoned);
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        base.Dispose();
        _drainSignal.Dispose();
    }
}

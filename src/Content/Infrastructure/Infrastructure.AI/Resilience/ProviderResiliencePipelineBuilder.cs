using Application.AI.Common.Interfaces.Resilience;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Resilience;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Infrastructure.AI.Resilience;

/// <summary>
/// Builds a per-provider resilience pipeline for LLM chat completion calls.
/// Each provider gets independent retry, circuit breaker, and timeout strategies.
/// </summary>
/// <remarks>
/// <para>
/// Strategy composition order (outermost to innermost):
/// <list type="number">
///   <item><description>Retry — wraps circuit breaker and timeout</description></item>
///   <item><description>Circuit Breaker — wraps timeout</description></item>
///   <item><description>Timeout — per-attempt deadline (innermost)</description></item>
/// </list>
/// </para>
/// <para>
/// This means: a single attempt has a timeout. If it fails, the circuit breaker records
/// the failure. If retries remain, the retry strategy tries again (going back through
/// circuit breaker and timeout).
/// </para>
/// <para>
/// <b>What gets retried is decided by <see cref="IProviderErrorClassifier"/>, not by exception
/// type.</b> Provider SDKs throw different exception types for the same HTTP status, so a
/// type-based predicate covers only whichever provider happens to match it and silently
/// ignores the rest. The classifier reads the status out of any of them, which lets retry and
/// the circuit breaker disagree deliberately: a rejected credential is neither retried nor
/// counted against the provider's health, because an auth failure is not evidence the provider
/// is down.
/// </para>
/// </remarks>
public static class ProviderResiliencePipelineBuilder
{
    /// <summary>
    /// Creates a <see cref="ResiliencePipeline{ChatResponse}"/> for the named provider
    /// using the supplied resilience configuration.
    /// </summary>
    /// <param name="providerName">Logical name for this provider (used in OTel tags and circuit breaker isolation).</param>
    /// <param name="config">Resilience configuration from Options pattern.</param>
    /// <param name="classifier">Decides which failures are retried and which count toward the circuit breaker.</param>
    /// <param name="circuitBreakerStateProvider">Output: the Polly state provider for this pipeline, used by health monitor to query circuit state.</param>
    /// <param name="onCircuitStateChanged">Optional callback invoked on circuit state transitions (Opened→Unavailable, Closed→Healthy, HalfOpened→Degraded).</param>
    /// <param name="logger">Logger for retry/circuit events.</param>
    /// <returns>A configured resilience pipeline scoped to this provider.</returns>
    public static ResiliencePipeline<ChatResponse> Build(
        string providerName,
        ResilienceConfig config,
        IProviderErrorClassifier classifier,
        out CircuitBreakerStateProvider circuitBreakerStateProvider,
        Action<ProviderHealthState>? onCircuitStateChanged = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        var stateProvider = new CircuitBreakerStateProvider();
        circuitBreakerStateProvider = stateProvider;

        var pipeline = new ResiliencePipelineBuilder<ChatResponse>()
            .AddRetry(CreateRetryOptions(providerName, config.Retry, classifier, logger))
            .AddCircuitBreaker(CreateCircuitBreakerOptions(providerName, config.CircuitBreaker, classifier, stateProvider, onCircuitStateChanged, logger))
            .AddTimeout(CreateTimeoutOptions(config.Timeout))
            .Build();

        return pipeline;
    }

    /// <summary>
    /// Builds a non-generic resilience pipeline for wrapping stream initiation.
    /// Uses the same retry/circuit/timeout config but operates on the void-returning
    /// initiation call rather than the stream content.
    /// </summary>
    /// <remarks>
    /// This pipeline has an independent circuit breaker from the typed pipeline built via
    /// <see cref="Build"/>. The <paramref name="sharedStateProvider"/> is used for read-only
    /// state queries by the health monitor only — it does not synchronize circuit state
    /// between the two pipelines. In practice, providers that fail streaming will independently
    /// trip this pipeline's circuit, while non-streaming failures trip the typed pipeline's circuit.
    /// </remarks>
    /// <param name="providerName">Logical name for this provider.</param>
    /// <param name="config">Resilience configuration.</param>
    /// <param name="classifier">Decides which failures are retried and which count toward the circuit breaker.</param>
    /// <param name="sharedStateProvider">State provider for read-only health queries. Does not synchronize circuit state across pipelines.</param>
    /// <param name="onCircuitStateChanged">Optional callback invoked on circuit state transitions.</param>
    /// <param name="logger">Logger for retry/circuit events.</param>
    /// <returns>A non-generic resilience pipeline for stream initiation.</returns>
    public static ResiliencePipeline BuildForStreamInitiation(
        string providerName,
        ResilienceConfig config,
        IProviderErrorClassifier classifier,
        CircuitBreakerStateProvider sharedStateProvider,
        Action<ProviderHealthState>? onCircuitStateChanged = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = Math.Max(1, config.Retry.MaxAttempts - 1),
                Delay = TimeSpan.FromSeconds(config.Retry.BaseDelaySeconds),
                BackoffType = ParseBackoffType(config.Retry.BackoffType),
                UseJitter = true,
                ShouldHandle = args => new ValueTask<bool>(ShouldRetry(classifier, args.Outcome.Exception)),
                OnRetry = args =>
                {
                    RecordRetry(providerName, args.AttemptNumber, args.Outcome.Exception, logger);
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = config.CircuitBreaker.FailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(config.CircuitBreaker.SamplingDurationSeconds),
                MinimumThroughput = config.CircuitBreaker.MinimumThroughput,
                BreakDuration = TimeSpan.FromSeconds(config.CircuitBreaker.BreakDurationSeconds),
                StateProvider = sharedStateProvider,
                ShouldHandle = args => new ValueTask<bool>(ShouldCountTowardBreaker(classifier, args.Outcome.Exception)),
                OnOpened = args =>
                {
                    if (onCircuitStateChanged is not null)
                        onCircuitStateChanged(ProviderHealthState.Unavailable);
                    else
                        RecordCircuitOpened(providerName, logger);
                    return default;
                },
                OnClosed = args =>
                {
                    if (onCircuitStateChanged is not null)
                        onCircuitStateChanged(ProviderHealthState.Healthy);
                    else
                        RecordCircuitClosed(providerName, logger);
                    return default;
                },
                OnHalfOpened = args =>
                {
                    logger?.LogInformation("Stream circuit half-opened for provider {Provider}", providerName);
                    onCircuitStateChanged?.Invoke(ProviderHealthState.Degraded);
                    return default;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(config.Timeout.PerAttemptSeconds))
            .Build();

        return pipeline;
    }

    private static RetryStrategyOptions<ChatResponse> CreateRetryOptions(
        string providerName, RetryConfig retryConfig, IProviderErrorClassifier classifier, ILogger? logger)
    {
        return new RetryStrategyOptions<ChatResponse>
        {
            MaxRetryAttempts = Math.Max(1, retryConfig.MaxAttempts - 1),
            Delay = TimeSpan.FromSeconds(retryConfig.BaseDelaySeconds),
            BackoffType = ParseBackoffType(retryConfig.BackoffType),
            UseJitter = true,
            ShouldHandle = args => new ValueTask<bool>(ShouldRetry(classifier, args.Outcome.Exception)),
            OnRetry = args =>
            {
                RecordRetry(providerName, args.AttemptNumber, args.Outcome.Exception, logger);
                return default;
            }
        };
    }

    private static CircuitBreakerStrategyOptions<ChatResponse> CreateCircuitBreakerOptions(
        string providerName, CircuitBreakerConfig cbConfig, IProviderErrorClassifier classifier, CircuitBreakerStateProvider stateProvider, Action<ProviderHealthState>? onCircuitStateChanged, ILogger? logger)
    {
        return new CircuitBreakerStrategyOptions<ChatResponse>
        {
            FailureRatio = cbConfig.FailureRatio,
            SamplingDuration = TimeSpan.FromSeconds(cbConfig.SamplingDurationSeconds),
            MinimumThroughput = cbConfig.MinimumThroughput,
            BreakDuration = TimeSpan.FromSeconds(cbConfig.BreakDurationSeconds),
            StateProvider = stateProvider,
            ShouldHandle = args => new ValueTask<bool>(ShouldCountTowardBreaker(classifier, args.Outcome.Exception)),
            OnOpened = args =>
            {
                if (onCircuitStateChanged is not null)
                    onCircuitStateChanged(ProviderHealthState.Unavailable);
                else
                    RecordCircuitOpened(providerName, logger);
                return default;
            },
            OnClosed = args =>
            {
                if (onCircuitStateChanged is not null)
                    onCircuitStateChanged(ProviderHealthState.Healthy);
                else
                    RecordCircuitClosed(providerName, logger);
                return default;
            },
            OnHalfOpened = args =>
            {
                logger?.LogInformation("Circuit half-opened for provider {Provider}", providerName);
                onCircuitStateChanged?.Invoke(ProviderHealthState.Degraded);
                return default;
            }
        };
    }

    /// <summary>
    /// Retry only what the classifier positively identifies as transient. An unrecognised
    /// failure is not repeated: retrying something we cannot name is as likely to be a bug in
    /// our own code as a provider blip, and repeating a non-idempotent call has a real cost.
    /// </summary>
    private static bool ShouldRetry(IProviderErrorClassifier classifier, Exception? exception)
        => exception is not null
           && classifier.Classify(exception).Kind == ProviderFailureKind.Transient;

    /// <summary>
    /// Count transient and unrecognised failures against the provider's health, but never a
    /// fatal one. A rejected credential or an exhausted balance says nothing about whether the
    /// provider is up, and letting it trip the breaker is exactly what buries the real cause
    /// under "circuit open for provider X".
    /// </summary>
    private static bool ShouldCountTowardBreaker(IProviderErrorClassifier classifier, Exception? exception)
        => exception is not null
           && classifier.Classify(exception).Kind is ProviderFailureKind.Transient or ProviderFailureKind.Unknown;

    private static TimeoutStrategyOptions CreateTimeoutOptions(TimeoutConfig timeoutConfig)
    {
        return new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(timeoutConfig.PerAttemptSeconds)
        };
    }

    private static DelayBackoffType ParseBackoffType(string backoffType)
    {
        return backoffType.ToLowerInvariant() switch
        {
            "linear" => DelayBackoffType.Linear,
            "exponential" => DelayBackoffType.Exponential,
            "constant" => DelayBackoffType.Constant,
            _ => throw new ArgumentException($"Unknown backoff type: '{backoffType}'. Valid: Linear, Exponential, Constant")
        };
    }

    private static void RecordRetry(string providerName, int attemptNumber, Exception? exception, ILogger? logger)
    {
        ResilienceMetrics.RetryAttempts.Add(1,
            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName));

        logger?.LogWarning("Retry {Attempt} for provider {Provider}: {Exception}",
            attemptNumber, providerName, exception?.Message);
    }

    private static void RecordCircuitOpened(string providerName, ILogger? logger)
    {
        ResilienceMetrics.CircuitStateChanges.Add(1,
            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName),
            new KeyValuePair<string, object?>(ResilienceConventions.TransitionFrom, ResilienceConventions.HealthValues.Healthy),
            new KeyValuePair<string, object?>(ResilienceConventions.TransitionTo, ResilienceConventions.HealthValues.Unavailable));

        logger?.LogError("Circuit opened for provider {Provider}", providerName);
    }

    private static void RecordCircuitClosed(string providerName, ILogger? logger)
    {
        ResilienceMetrics.CircuitStateChanges.Add(1,
            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName),
            new KeyValuePair<string, object?>(ResilienceConventions.TransitionFrom, ResilienceConventions.HealthValues.Degraded),
            new KeyValuePair<string, object?>(ResilienceConventions.TransitionTo, ResilienceConventions.HealthValues.Healthy));

        logger?.LogInformation("Circuit closed for provider {Provider}", providerName);
    }
}

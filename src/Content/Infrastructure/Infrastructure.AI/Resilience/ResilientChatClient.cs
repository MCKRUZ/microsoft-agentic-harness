using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Resilience;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Resilience;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;

namespace Infrastructure.AI.Resilience;

/// <summary>
/// An <see cref="IChatClient"/> wrapper that iterates through an ordered provider fallback chain,
/// executing each provider through its own per-provider Polly resilience pipeline. Transparently
/// handles retries, circuit breaker tripping, provider failover, and <see cref="FallbackMetadata"/>
/// attachment to responses.
/// </summary>
/// <remarks>
/// <para>
/// The cross-provider fallback is a simple iteration loop, NOT a Polly fallback strategy.
/// Each provider's pipeline handles per-provider retry, circuit breaking, and timeout.
/// When a provider's pipeline throws (meaning all retries are exhausted or circuit is open),
/// the loop advances to the next provider.
/// </para>
/// <para>
/// Thread-safe for concurrent calls — all mutable state is per-call (stack-local).
/// </para>
/// <para>
/// Streaming fallback note: if a provider fails mid-stream after yielding chunks, those chunks
/// are already delivered to the consumer. The next provider starts a fresh stream. Consumers
/// needing atomic responses should use <see cref="GetResponseAsync"/> instead.
/// </para>
/// <para>
/// <b>Not every failure is worth falling back on.</b> When
/// <see cref="IProviderErrorClassifier"/> reports a failure as fatal for the whole chain — a
/// rejected credential, an exhausted balance, a disabled account — the loop stops immediately
/// and throws <see cref="ProviderFatalErrorException"/>. Rotating providers cannot fix a cause
/// that lives in shared configuration; it only spends wall-clock and replaces an actionable
/// error with "all providers exhausted".
/// </para>
/// <para>
/// <b>A caller cancellation is not a provider failure at all.</b> When the classifier reports
/// <see cref="ProviderFailureClassification.IsCallerCancellation"/>, the loop stops immediately
/// and rethrows the original exception unchanged — no <see cref="ProviderFatalErrorException"/>,
/// no <see cref="ProviderExhaustedException"/>, no attempt at the next provider. Nobody is
/// waiting for a response, so advancing the chain would only spend wall-clock, and wrapping the
/// exception would misreport a withdrawal as a provider or chain failure.
/// </para>
/// </remarks>
public sealed class ResilientChatClient : IChatClient
{
    /// <summary>Well-known key for <see cref="FallbackMetadata"/> in response additional properties.</summary>
    public const string FallbackMetadataKey = "FallbackMetadata";

    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<ProviderEntry> _providers;
    private readonly IProviderHealthMonitor _healthMonitor;
    private readonly IProviderErrorClassifier _errorClassifier;
    private readonly ILogger<ResilientChatClient>? _logger;

    /// <summary>
    /// Creates a resilient chat client wrapping an ordered provider fallback chain.
    /// </summary>
    /// <param name="providers">Ordered provider entries. First is primary, rest are fallbacks.</param>
    /// <param name="healthMonitor">Provides circuit breaker health state for skip-on-open logic.</param>
    /// <param name="errorClassifier">Decides whether a provider failure is worth falling back on.</param>
    /// <param name="logger">Optional logger.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="providers"/> is empty.</exception>
    public ResilientChatClient(
        IReadOnlyList<ProviderEntry> providers,
        IProviderHealthMonitor healthMonitor,
        IProviderErrorClassifier errorClassifier,
        ILogger<ResilientChatClient>? logger = null)
    {
        if (providers.Count == 0)
            throw new ArgumentException("At least one provider is required.", nameof(providers));

        ArgumentNullException.ThrowIfNull(errorClassifier);

        _providers = providers;
        _healthMonitor = healthMonitor;
        _errorClassifier = errorClassifier;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var failedProviders = new List<string>();
        Exception? lastException = null;

        foreach (var provider in _providers)
        {
            if (SkipIfCircuitOpen(provider, failedProviders, isStreaming: false))
                continue;

            try
            {
                var response = await provider.Pipeline.ExecuteAsync(
                    async ct => await provider.Client.GetResponseAsync(messages, options, ct),
                    cancellationToken);

                AttachMetadata(response, provider.Name, failedProviders);

                if (failedProviders.Count > 0)
                {
                    ResilienceMetrics.FallbackActivations.Add(1,
                        new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, provider.Name));
                }

                return response;
            }
            catch (Exception ex)
            {
                var classification = _errorClassifier.Classify(ex, cancellationToken);

                if (classification.IsCallerCancellation)
                    throw;

                if (TryBuildChainFatal(ex, classification, provider.Name, failedProviders) is { } fatal)
                    throw fatal;

                lastException = ex;
                _logger?.LogWarning(ex, "Provider {Provider} failed, attempting next", provider.Name);
                failedProviders.Add(provider.Name);
            }
        }

        ResilienceMetrics.DegradationEvents.Add(1);
        throw lastException is not null
            ? new ProviderExhaustedException(failedProviders, DefaultRetryAfter, lastException)
            : new ProviderExhaustedException(failedProviders, DefaultRetryAfter);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var failedProviders = new List<string>();
        Exception? lastException = null;

        foreach (var provider in _providers)
        {
            if (SkipIfCircuitOpen(provider, failedProviders, isStreaming: true))
                continue;

            var succeeded = false;

            // Drive the FIRST network interaction (the real request) inside the resilience
            // pipeline. IChatClient.GetStreamingResponseAsync only builds a lazy iterator and
            // performs no I/O, so wrapping the bare call leaves retry/circuit-breaker/timeout
            // observing only instant successes. Priming the enumerator here surfaces
            // connection/auth/timeout failures to the pipeline and the health monitor.
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            var hasFirst = false;
            try
            {
                await provider.StreamPipeline.ExecuteAsync(async _ =>
                {
                    // A retry re-invokes this delegate; dispose any enumerator from the prior attempt.
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync();
                        enumerator = null;
                        hasFirst = false;
                    }

                    // Create the stream with the long-lived outer token, NOT Polly's per-attempt
                    // token: that token's CancellationTokenSource is recycled once ExecuteAsync
                    // returns, but the stream is enumerated afterwards in the yield loop below.
                    enumerator = provider.Client
                        .GetStreamingResponseAsync(messages, options, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                    hasFirst = await enumerator.MoveNextAsync();
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                // Build the decision before disposing, but act after — an abandoned enumerator
                // from the failed attempt must still be released regardless of which way this
                // goes. TryBuildChainFatal is safe to call unconditionally here: it already
                // returns null for any classification that isn't StopsChain, CallerCancelled
                // included, the same as the other two catch blocks in this class rely on.
                var classification = _errorClassifier.Classify(ex, cancellationToken);
                var fatal = TryBuildChainFatal(ex, classification, provider.Name, failedProviders);

                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync();
                }

                if (classification.IsCallerCancellation)
                    throw;

                if (fatal is not null)
                    throw fatal;

                lastException = ex;
                _logger?.LogWarning(ex, "Stream initiation failed for provider {Provider}", provider.Name);
                failedProviders.Add(provider.Name);
                continue;
            }

            try
            {
                if (hasFirst)
                {
                    yield return enumerator!.Current;

                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await enumerator!.MoveNextAsync();
                        }
                        catch (Exception ex)
                        {
                            // The enclosing finally releases the enumerator on the way out.
                            var classification = _errorClassifier.Classify(ex, cancellationToken);

                            if (classification.IsCallerCancellation)
                                throw;

                            if (TryBuildChainFatal(ex, classification, provider.Name, failedProviders) is { } fatal)
                                throw fatal;

                            lastException = ex;
                            _logger?.LogWarning(ex, "Mid-stream failure for provider {Provider}", provider.Name);
                            failedProviders.Add(provider.Name);
                            break;
                        }

                        if (!hasNext)
                        {
                            succeeded = true;
                            break;
                        }

                        yield return enumerator!.Current;
                    }
                }
                else
                {
                    // Pipeline-confirmed initiation with an empty stream is a success.
                    succeeded = true;
                }
            }
            finally
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync();
                }
            }

            if (succeeded)
            {
                if (failedProviders.Count > 0)
                {
                    ResilienceMetrics.FallbackActivations.Add(1,
                        new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, provider.Name));
                }
                yield break;
            }
        }

        ResilienceMetrics.DegradationEvents.Add(1);
        throw lastException is not null
            ? new ProviderExhaustedException(failedProviders, DefaultRetryAfter, lastException)
            : new ProviderExhaustedException(failedProviders, DefaultRetryAfter);
    }

    /// <summary>
    /// Whether <paramref name="provider"/>'s circuit is open, recording the skip if so.
    /// </summary>
    /// <remarks>Shared by <see cref="GetResponseAsync"/> and <see cref="GetStreamingResponseAsync"/>, which
    /// otherwise duplicated this check with only their log wording differing.</remarks>
    private bool SkipIfCircuitOpen(ProviderEntry provider, List<string> failedProviders, bool isStreaming)
    {
        if (_healthMonitor.GetProviderHealth(provider.Name) != ProviderHealthState.Unavailable)
            return false;

        _logger?.LogDebug(
            isStreaming ? "Skipping streaming provider {Provider} — circuit open" : "Skipping provider {Provider} — circuit open",
            provider.Name);
        failedProviders.Add(provider.Name);
        return true;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClientMetadata))
            return new ChatClientMetadata(nameof(ResilientChatClient));
        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Client.Dispose();
        }
    }

    /// <summary>
    /// Converts a failure into the exception that must abandon the chain, or
    /// <see langword="null"/> when the chain should continue to the next provider.
    /// </summary>
    /// <remarks>
    /// Takes the classification rather than re-deriving it from <paramref name="exception"/>:
    /// every call site already classifies the exception once to decide
    /// <see cref="ProviderFailureClassification.IsCallerCancellation"/>, and classifying twice
    /// per failed attempt would repeat the bounded exception-chain walk and message scan for no
    /// reason.
    /// <para>
    /// The provider's own message is written to the log here and deliberately not carried on
    /// the thrown exception's message — provider error text has been observed to echo back
    /// credential fragments, and this exception may reach an API response.
    /// </para>
    /// </remarks>
    private ProviderFatalErrorException? TryBuildChainFatal(
        Exception exception,
        ProviderFailureClassification classification,
        string providerName,
        IReadOnlyList<string> failedProviders)
    {
        if (!classification.StopsChain)
            return null;

        // A replaced classifier can return a chain-fatal verdict with no reason code — the record
        // struct's constructor defaults it to null. Null-forgiving it here would hand a consumer
        // an exception whose non-nullable ReasonCode is null and whose sentence trails off at
        // "non-retryable error ()", failing at the moment the real error is being reported.
        var reasonCode = string.IsNullOrWhiteSpace(classification.ReasonCode)
            ? ProviderFatalReason.Configuration
            : classification.ReasonCode;

        var fatal = new ProviderFatalErrorException(
            providerName, reasonCode, exception, failedProviders);

        // Log the exception's own sentence rather than re-composing it. The operator-facing
        // wording then has exactly one author, and the log cannot drift from what the caller sees.
        _logger?.LogError(exception, "{FatalError} Provider message: {ProviderMessage}",
            fatal.Message, exception.Message);

        // The request failed outright, exactly as it does when the chain is exhausted, so it
        // belongs in the same counter — otherwise a fleet-wide bad credential looks like zero
        // degradation on the dashboard.
        ResilienceMetrics.DegradationEvents.Add(1);

        return fatal;
    }

    private void AttachMetadata(ChatResponse response, string activeProvider, List<string> failedProviders)
    {
        var metadata = new FallbackMetadata
        {
            ActiveProvider = activeProvider,
            IsFallback = failedProviders.Count > 0,
            FailedProviders = failedProviders.ToArray(),
            DisabledCapabilities = ImmutableHashSet<string>.Empty,
            CircuitStates = _healthMonitor.GetAllProviderHealth()
        };

        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties[FallbackMetadataKey] = metadata;
    }

    /// <summary>
    /// Represents a single provider entry in the fallback chain with its associated
    /// resilience pipelines.
    /// </summary>
    /// <param name="Name">Logical provider identifier (e.g., "azure-openai", "anthropic").</param>
    /// <param name="Client">The underlying chat client for this provider.</param>
    /// <param name="Pipeline">The per-provider typed Polly pipeline for <see cref="GetResponseAsync"/> calls.</param>
    /// <param name="StreamPipeline">The per-provider non-generic Polly pipeline for stream initiation. Built via <see cref="ProviderResiliencePipelineBuilder.BuildForStreamInitiation"/>.</param>
    public sealed record ProviderEntry(
        string Name,
        IChatClient Client,
        ResiliencePipeline<ChatResponse> Pipeline,
        ResiliencePipeline StreamPipeline);
}

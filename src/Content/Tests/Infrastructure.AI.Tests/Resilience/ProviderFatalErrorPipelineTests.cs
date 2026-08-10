using System.Net;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Polly.CircuitBreaker;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Verifies that a non-retryable provider failure consumes no retries and leaves the circuit
/// breaker untouched, while a genuinely transient failure still does both.
/// </summary>
/// <remarks>
/// Every assertion here is paired with a transient control run through the same pipeline. A
/// "fatal failure was not retried" result proves nothing on its own — it is equally consistent
/// with a pipeline that retries nothing at all, which is exactly the state the shipped
/// configuration was in before this work.
/// </remarks>
public sealed class ProviderFatalErrorPipelineTests
{
    [Fact]
    public async Task Pipeline_Unauthorized_CallsTheProviderExactlyOnce()
    {
        var config = CreateConfig(maxAttempts: 4);
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out _);
        var calls = 0;

        var act = async () => await pipeline.ExecuteAsync<ChatResponse>(async _ =>
        {
            calls++;
            throw new HttpRequestException("Invalid API key", null, HttpStatusCode.Unauthorized);
        }, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(1, "a rejected credential never succeeds on repetition");
    }

    [Fact]
    public async Task Pipeline_TooManyRequests_StillRetriesToConfiguredMax()
    {
        // The control for the test above: same pipeline, same attempt budget, transient status.
        var config = CreateConfig(maxAttempts: 4);
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out _);
        var calls = 0;

        var act = async () => await pipeline.ExecuteAsync<ChatResponse>(async _ =>
        {
            calls++;
            throw new HttpRequestException("Too many requests", null, HttpStatusCode.TooManyRequests);
        }, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        calls.Should().BeGreaterThan(1, "a rate limit is the archetypal retryable failure");
    }

    [Fact]
    public async Task Pipeline_RepeatedFatalFailures_LeaveTheCircuitClosed()
    {
        // Enough failures to trip the breaker several times over, were they counted at all.
        var config = CreateConfig(maxAttempts: 1, failureRatio: 0.5, minimumThroughput: 2);
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out var stateProvider);

        await RunFailures(pipeline, 8,
            () => new HttpRequestException("Invalid API key", null, HttpStatusCode.Unauthorized));

        stateProvider.CircuitState.Should().Be(
            CircuitState.Closed,
            "an auth failure is not evidence the provider is down, so it must not trip the breaker");
    }

    [Fact]
    public async Task Pipeline_RepeatedTransientFailures_OpenTheCircuit()
    {
        // The control: identical loop, identical count, transient status instead of fatal.
        var config = CreateConfig(maxAttempts: 1, failureRatio: 0.5, minimumThroughput: 2);
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out var stateProvider);

        await RunFailures(pipeline, 8,
            () => new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable));

        stateProvider.CircuitState.Should().Be(
            CircuitState.Open,
            "the breaker must still react to a provider that is genuinely failing");
    }

    [Fact]
    public async Task Pipeline_AzureOpenAiShapedRateLimit_IsRetried()
    {
        // The shipped fallback chain's primary provider throws ClientResultException, which the
        // pre-classifier predicate did not recognise — so nothing in the pipeline ever fired.
        var config = CreateConfig(maxAttempts: 3);
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out _);
        var calls = 0;

        var act = async () => await pipeline.ExecuteAsync<ChatResponse>(async _ =>
        {
            calls++;
            throw new System.ClientModel.ClientResultException(
                "Rate limit reached", new StubPipelineResponse(429));
        }, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        calls.Should().BeGreaterThan(1, "the primary provider's own exception type must reach the retry strategy");
    }

    [Fact]
    public async Task Pipeline_AzureInferenceShapedRateLimit_IsRetried()
    {
        // Same defect, the fallback provider's exception type.
        var config = CreateConfig(maxAttempts: 3);
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out _);
        var calls = 0;

        var act = async () => await pipeline.ExecuteAsync<ChatResponse>(async _ =>
        {
            calls++;
            throw new Azure.RequestFailedException(429, "Rate limit is exceeded");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        calls.Should().BeGreaterThan(1, "the fallback provider's own exception type must reach the retry strategy");
    }

    [Fact]
    public async Task StreamPipeline_Unauthorized_CallsTheProviderExactlyOnce()
    {
        // The streaming pipeline is built separately and had its own copy of the predicate —
        // the kind of duplication where a fix lands on one path and not the other.
        var config = CreateConfig(maxAttempts: 4);
        var pipeline = ProviderResiliencePipelineBuilder.BuildForStreamInitiation(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), new CircuitBreakerStateProvider());
        var calls = 0;

        var act = async () => await pipeline.ExecuteAsync(async _ =>
        {
            calls++;
            throw new HttpRequestException("Invalid API key", null, HttpStatusCode.Unauthorized);
        }, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task StreamPipeline_TooManyRequests_StillRetries()
    {
        var config = CreateConfig(maxAttempts: 4);
        var pipeline = ProviderResiliencePipelineBuilder.BuildForStreamInitiation(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), new CircuitBreakerStateProvider());
        var calls = 0;

        var act = async () => await pipeline.ExecuteAsync(async _ =>
        {
            calls++;
            throw new HttpRequestException("Too many requests", null, HttpStatusCode.TooManyRequests);
        }, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        calls.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Pipeline_CircuitAlreadyOpen_RejectsImmediatelyWithoutBurningRetries()
    {
        // The circuit breaker rejects with a BrokenCircuitException that *wraps the exception
        // which broke the circuit*. Classifying by unwrapping therefore finds the original 503,
        // calls it transient, and retries — sleeping through the entire backoff budget against a
        // breaker that by definition will not let anything through. Measured before the fix:
        // three attempts and the full delay, for a call that can only ever be rejected.
        var config = CreateConfig(maxAttempts: 4, failureRatio: 0.5, minimumThroughput: 2);
        config.Retry.BaseDelaySeconds = 0.4;
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out var stateProvider);

        await RunFailures(pipeline, 8,
            () => new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable));
        stateProvider.CircuitState.Should().Be(CircuitState.Open, "precondition for this test");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = async () => await pipeline.ExecuteAsync<ChatResponse>(
            async _ => throw new HttpRequestException("unreachable"), CancellationToken.None);
        await act.Should().ThrowAsync<BrokenCircuitException>();
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(
            400,
            "an open circuit must fail fast — retrying its own rejection cannot possibly succeed");
    }

    [Fact]
    public async Task Pipeline_CircuitRejection_DoesNotFeedBackIntoTheFailureRatio()
    {
        // A breaker that counted its own rejections would hold itself open indefinitely.
        var config = CreateConfig(maxAttempts: 1, failureRatio: 0.5, minimumThroughput: 2);
        var classifier = ResilienceTestSupport.CreateClassifier(config);

        var rejection = new BrokenCircuitException(
            "circuit is open",
            new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable));

        var classification = classifier.Classify(rejection);

        classification.Kind.Should().Be(
            Domain.AI.Resilience.ProviderFailureKind.FatalForProvider,
            "not retryable and not countable, but the next provider should still be tried");
        classification.ReasonCode.Should().Be(Domain.AI.Resilience.ProviderFatalReason.CircuitOpen);
    }

    [Fact]
    public async Task Pipeline_PerAttemptTimeout_IsStillRetried()
    {
        // The control for the two tests above. TimeoutRejectedException shares a base class
        // (ExecutionRejectedException) with BrokenCircuitException, so excluding failures by
        // that base — the obvious fix — would silently stop retrying timeouts, which are the
        // most ordinary transient failure there is.
        var config = CreateConfig(maxAttempts: 3);
        config.Timeout.PerAttemptSeconds = 1;
        var pipeline = ProviderResiliencePipelineBuilder.Build(
            "test-provider", config, ResilienceTestSupport.CreateClassifier(config), out _);
        var calls = 0;

        var act = async () => await pipeline.ExecuteAsync<ChatResponse>(async ct =>
        {
            calls++;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            throw new InvalidOperationException("unreachable");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<Polly.Timeout.TimeoutRejectedException>();
        calls.Should().BeGreaterThan(1, "a per-attempt timeout must still consume retries");
    }

    private static async Task RunFailures(
        Polly.ResiliencePipeline<ChatResponse> pipeline, int count, Func<Exception> failure)
    {
        for (var i = 0; i < count; i++)
        {
            try
            {
                await pipeline.ExecuteAsync<ChatResponse>(_ => throw failure(), CancellationToken.None);
            }
            catch
            {
                // The failure itself is the point; the assertion is on circuit state.
            }
        }
    }

    private static ResilienceConfig CreateConfig(
        int maxAttempts = 2,
        double failureRatio = 0.5,
        int minimumThroughput = 5)
        => new()
        {
            Enabled = true,
            Retry = new RetryConfig
            {
                MaxAttempts = maxAttempts,
                BaseDelaySeconds = 0.01,
                BackoffType = "Exponential"
            },
            CircuitBreaker = new CircuitBreakerConfig
            {
                FailureRatio = failureRatio,
                SamplingDurationSeconds = 30,
                MinimumThroughput = minimumThroughput,
                BreakDurationSeconds = 60
            },
            Timeout = new TimeoutConfig { PerAttemptSeconds = 30 }
        };
}

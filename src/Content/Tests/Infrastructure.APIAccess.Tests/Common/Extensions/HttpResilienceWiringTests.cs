using CorrelationId.DependencyInjection;
using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Common.Extensions;
using ApiAccessExtensions = Infrastructure.APIAccess.Common.Extensions.IServiceCollectionExtensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Common.Extensions;

/// <summary>
/// Proves the HTTP resilience pipelines actually execute on the live <see cref="HttpClient"/>
/// path (audit item F3). Pre-fix, <c>AddResiliencePipelines</c> registered named pipelines in
/// the Polly registry that no client ever executed, and the retry <c>ShouldHandle</c> predicate
/// inspected the Polly ARGUMENTS struct type instead of the thrown EXCEPTION type
/// (<c>RetryableExceptions.Contains(args.GetType())</c> is always false), so retries could never
/// fire even if the pipeline had been attached. These tests drive a real
/// <see cref="IHttpClientFactory"/> client whose primary handler counts attempts.
/// </summary>
public sealed class HttpResilienceWiringTests
{
    /// <summary>
    /// Terminal handler that records every send attempt and always throws the configured
    /// exception, simulating a persistently failing downstream service.
    /// </summary>
    private sealed class CountingFailingHandler : HttpMessageHandler
    {
        private int _attempts;

        /// <summary>Total number of HTTP send attempts observed.</summary>
        public int Attempts => _attempts;

        /// <summary>Exception thrown on every attempt.</summary>
        public Func<Exception> ExceptionFactory { get; init; } =
            () => new HttpRequestException("simulated transient network failure");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw ExceptionFactory();
        }
    }

    private static ServiceProvider BuildProvider(CountingFailingHandler primaryHandler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDefaultCorrelationId(options => options.AddToLoggingScope = true);
        services.Configure<HttpConfig>(config =>
        {
            // 2 retries, near-zero delay so the test completes quickly.
            config.Policies.HttpRetry.Count = 2;
            config.Policies.HttpRetry.Delay = TimeSpan.FromMilliseconds(1);
        });

        // The production wiring under test: defaults applied to every factory-created client.
        services.AddDefaultHttpClient();

        // A canary client whose primary handler counts attempts instead of hitting the network.
        services.AddHttpClient("resilience-canary")
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HttpClient_TransientNetworkFailure_RetriesThroughAttachedPipeline()
    {
        // 1 initial attempt + HttpRetry.Count retries. Pre-fix this observes exactly 1 attempt:
        // the registry pipeline was never attached to any client AND the ShouldHandle predicate
        // could never match the exception.
        var handler = new CountingFailingHandler();
        await using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("resilience-canary");

        var act = () => client.GetAsync("http://localhost/canary");

        await act.Should().ThrowAsync<HttpRequestException>(
            "the failure is persistent, so retries must eventually surface the original exception");
        handler.Attempts.Should().Be(3,
            "a transient HttpRequestException must be retried HttpRetry.Count (2) times " +
            "after the initial attempt by the attached resilience pipeline");
    }

    [Fact]
    public async Task HttpClient_TransientFailureOnPost_DoesNotRetry()
    {
        // POST is not idempotent: a "transient" HttpRequestException can surface AFTER the
        // server processed the request (connection dropped on the response path). Retrying
        // would duplicate side effects (A2A messages, GitOps syncs, connector mutations,
        // webhooks). Only idempotent methods (GET/HEAD/OPTIONS) may be retried.
        var handler = new CountingFailingHandler();
        await using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("resilience-canary");

        var act = () => client.PostAsync("http://localhost/canary", new StringContent("payload"));

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Attempts.Should().Be(1,
            "non-idempotent requests must never be retried, even on transient network failures");
    }

    [Fact]
    public void HttpClient_OuterTimeout_IsInfinite_SoPipelineOwnsTimeouts()
    {
        // If HttpClient.Timeout equals the pipeline's per-attempt timeout, timeout-triggered
        // retries are dead code (the outer timeout cancels the whole operation first). The
        // pipeline owns both the per-attempt and the total timeout; the client must not race it.
        var handler = new CountingFailingHandler();
        using var provider = BuildProvider(handler);

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("resilience-canary");

        client.Timeout.Should().Be(System.Threading.Timeout.InfiniteTimeSpan,
            "the resilience pipeline's total-timeout strategy bounds the request, not HttpClient.Timeout");
    }

    [Fact]
    public async Task HttpClient_NonRetryableFailure_DoesNotRetry()
    {
        // The predicate must stay selective: only the declared retryable exception types
        // (network + resilience-strategy exceptions) are retried.
        var handler = new CountingFailingHandler
        {
            ExceptionFactory = () => new InvalidOperationException("non-transient bug")
        };
        await using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("resilience-canary");

        var act = () => client.GetAsync("http://localhost/canary");

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Attempts.Should().Be(1,
            "non-retryable exceptions must not trigger retries");
    }

    // ---------------------------------------------------------------------
    // Retry predicate unit tests (IsRetryableHttpFailure drives ShouldHandle)
    // ---------------------------------------------------------------------

    [Fact]
    public void IsRetryableHttpFailure_RateLimiterRejection_IsNotRetried()
    {
        // The rate limiter sits INSIDE this same pipeline: retrying its rejection only burns
        // exponential backoff on a self-inflicted rejection. Callers must fast-fail.
        ApiAccessExtensions
            .IsRetryableHttpFailure(new Polly.RateLimiting.RateLimiterRejectedException(), HttpMethod.Get)
            .Should().BeFalse("rate-limiter rejections are self-inflicted and must not be retried");
    }

    [Fact]
    public void IsRetryableHttpFailure_BrokenCircuit_IsNotRetried()
    {
        ApiAccessExtensions
            .IsRetryableHttpFailure(new Polly.CircuitBreaker.BrokenCircuitException(), HttpMethod.Get)
            .Should().BeFalse("an open circuit means the downstream is known-bad; retrying inside the same pipeline is pointless");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void IsRetryableHttpFailure_TransientExceptionOnIdempotentMethod_IsRetried(string method)
    {
        ApiAccessExtensions
            .IsRetryableHttpFailure(new HttpRequestException("transient"), new HttpMethod(method))
            .Should().BeTrue();
        ApiAccessExtensions
            .IsRetryableHttpFailure(new Polly.Timeout.TimeoutRejectedException(), new HttpMethod(method))
            .Should().BeTrue("the per-attempt timeout sits inside the retry strategy — retrying a timed-out attempt is its purpose");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IsRetryableHttpFailure_TransientExceptionOnNonIdempotentMethod_IsNotRetried(string method)
    {
        ApiAccessExtensions
            .IsRetryableHttpFailure(new HttpRequestException("transient"), new HttpMethod(method))
            .Should().BeFalse("a transient failure can surface after the server processed the request; retrying duplicates side effects");
    }

    [Fact]
    public void IsRetryableHttpFailure_UnknownMethodOrNoException_IsNotRetried()
    {
        ApiAccessExtensions
            .IsRetryableHttpFailure(new HttpRequestException("transient"), requestMethod: null)
            .Should().BeFalse("without a known request method, retrying cannot be proven safe");
        ApiAccessExtensions
            .IsRetryableHttpFailure(exception: null, HttpMethod.Get)
            .Should().BeFalse();
    }
}

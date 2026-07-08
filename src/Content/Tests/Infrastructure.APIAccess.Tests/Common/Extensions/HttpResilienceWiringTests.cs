using CorrelationId.DependencyInjection;
using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Common.Extensions;
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
}

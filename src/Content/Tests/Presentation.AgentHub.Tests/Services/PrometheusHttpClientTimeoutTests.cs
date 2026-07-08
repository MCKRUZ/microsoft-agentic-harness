using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Interfaces;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// Verifies the Prometheus typed <see cref="System.Net.Http.HttpClient"/> follows the harness
/// resilience pipeline's timeout model. Because the typed client is created via
/// <see cref="System.Net.Http.IHttpClientFactory"/> it inherits the resilience pipeline
/// (per-attempt + total timeout) attached to every factory-created client by
/// <c>AddDefaultHttpClient</c>. A finite <c>HttpClient.Timeout</c> would race that pipeline and
/// could truncate the retry budget mid-attempt, so the registration must leave the client timeout
/// infinite — consistent with the default (non-typed) clients.
/// </summary>
public sealed class PrometheusHttpClientTimeoutTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public PrometheusHttpClientTimeoutTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void PrometheusTypedClient_TimeoutIsInfinite_SoResiliencePipelineOwnsIt()
    {
        var httpClientFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();

        // The typed-client logical name for AddHttpClient<TClient, TImplementation> is the
        // TClient type name.
        var client = httpClientFactory.CreateClient(nameof(IPrometheusQueryService));

        client.Timeout.Should().Be(
            Timeout.InfiniteTimeSpan,
            "the resilience pipeline owns the timeout; the typed Prometheus client must not set a " +
            "finite one that races it");
    }
}

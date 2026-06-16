using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Presentation.AgentHub.Services;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// Regression tests for the 2026-06-11 solution review finding confirmed[63]:
/// <see cref="PrometheusQueryService"/> must not surface raw exception messages
/// (Prometheus host/URL, connection diagnostics, deserialization internals) to callers.
/// Both <c>ExecuteQueryAsync</c> and <c>GetHealthAsync</c> previously assigned
/// <c>Error = ex.Message</c>; they now return stable, scrubbed messages while logging
/// the full exception.
/// </summary>
public sealed class PrometheusQueryServiceSolutionReviewFixTests
{
    private const string SensitiveMarker = "secret-prometheus-host:9090/internal-diagnostics";

    private static PrometheusQueryService CreateService(FakePrometheusHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:9090/"),
        };
        return new PrometheusQueryService(httpClient, NullLogger<PrometheusQueryService>.Instance);
    }

    [Fact]
    public async Task QueryInstantAsync_QueryThrows_DoesNotLeakExceptionMessage()
    {
        // Arrange: an unexpected exception whose message carries internal detail.
        var handler = new FakePrometheusHandler(new InvalidOperationException(SensitiveMarker));
        var service = CreateService(handler);

        // Act
        var result = await service.QueryInstantAsync("up");

        // Assert: failure is reported, but the raw exception message is scrubbed.
        result.Success.Should().BeFalse();
        result.Error.Should().Be(PrometheusQueryService.GenericQueryErrorMessage);
        result.Error.Should().NotContain(SensitiveMarker);
    }

    [Fact]
    public async Task QueryInstantAsync_InvalidJson_ReturnsScrubbedErrorNotDeserializerInternals()
    {
        // Arrange: malformed payload triggers a JSON exception in the catch-all.
        var handler = new FakePrometheusHandler(HttpStatusCode.OK, "{ this is not valid json");
        var service = CreateService(handler);

        // Act
        var result = await service.QueryInstantAsync("up");

        // Assert: deserialization internals (line numbers, byte positions) are not surfaced.
        result.Success.Should().BeFalse();
        result.Error.Should().Be(PrometheusQueryService.GenericQueryErrorMessage);
    }

    [Fact]
    public async Task GetHealthAsync_PrometheusUnreachable_DoesNotLeakExceptionMessage()
    {
        // Arrange: connection failure whose message carries internal host detail.
        var handler = new FakePrometheusHandler(new HttpRequestException(SensitiveMarker));
        var service = CreateService(handler);

        // Act
        var result = await service.GetHealthAsync();

        // Assert: unhealthy is reported, but the raw exception message is scrubbed.
        result.Healthy.Should().BeFalse();
        result.Error.Should().Be(PrometheusQueryService.GenericHealthErrorMessage);
        result.Error.Should().NotContain(SensitiveMarker);
    }
}

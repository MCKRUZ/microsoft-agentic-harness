using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Services;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

public sealed class PrometheusQueryServiceTests
{
    private static PrometheusQueryService CreateService(FakePrometheusHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:9090/"),
        };
        return new PrometheusQueryService(httpClient, NullLogger<PrometheusQueryService>.Instance);
    }

    private static FakePrometheusHandler SuccessHandler(object responseBody) =>
        new(HttpStatusCode.OK, JsonSerializer.Serialize(responseBody));

    private static FakePrometheusHandler ErrorHandler(HttpStatusCode code, string body = "{}") =>
        new(code, body);

    // --- QueryInstantAsync ---

    [Fact]
    public async Task QueryInstantAsync_SuccessResponse_ReturnsNormalizedSeries()
    {
        var prometheusResponse = new
        {
            status = "success",
            data = new
            {
                resultType = "vector",
                result = new[]
                {
                    new
                    {
                        metric = new Dictionary<string, string> { ["__name__"] = "up", ["job"] = "app" },
                        value = new object[] { 1700000000.0, "1" },
                    },
                },
            },
        };
        var service = CreateService(SuccessHandler(prometheusResponse));

        var result = await service.QueryInstantAsync("up");

        result.Success.Should().BeTrue();
        result.ResultType.Should().Be("vector");
        result.Series.Should().HaveCount(1);
        result.Series[0].Labels.Should().ContainKey("__name__");
        result.Series[0].DataPoints.Should().HaveCount(1);
        result.Series[0].DataPoints[0].Value.Should().Be("1");
    }

    [Fact]
    public async Task QueryInstantAsync_WithTime_IncludesTimeInRequest()
    {
        var handler = SuccessHandler(new { status = "success", data = new { resultType = "vector", result = Array.Empty<object>() } });
        var service = CreateService(handler);

        await service.QueryInstantAsync("up", time: "1700000000");

        handler.LastRequestUri.Should().Contain("time=1700000000");
    }

    [Fact]
    public async Task QueryInstantAsync_PrometheusError_ReturnsFailure()
    {
        var errorResponse = new { status = "error", errorType = "bad_data", error = "invalid expression" };
        var service = CreateService(SuccessHandler(errorResponse));

        var result = await service.QueryInstantAsync("invalid{");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid expression");
    }

    [Fact]
    public async Task QueryInstantAsync_HttpFailure_ReturnsFailure()
    {
        var service = CreateService(ErrorHandler(HttpStatusCode.InternalServerError, "Server Error"));

        var result = await service.QueryInstantAsync("up");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task QueryInstantAsync_InvalidJson_ReturnsFailure()
    {
        var service = CreateService(new FakePrometheusHandler(HttpStatusCode.OK, "not json"));

        var result = await service.QueryInstantAsync("up");

        result.Success.Should().BeFalse();
    }

    // --- QueryRangeAsync ---

    [Fact]
    public async Task QueryRangeAsync_SuccessResponse_ReturnsMultipleDataPoints()
    {
        var prometheusResponse = new
        {
            status = "success",
            data = new
            {
                resultType = "matrix",
                result = new[]
                {
                    new
                    {
                        metric = new Dictionary<string, string> { ["__name__"] = "up" },
                        values = new[]
                        {
                            new object[] { 1700000000.0, "1" },
                            new object[] { 1700000015.0, "1" },
                            new object[] { 1700000030.0, "0" },
                        },
                    },
                },
            },
        };
        var service = CreateService(SuccessHandler(prometheusResponse));

        var result = await service.QueryRangeAsync("up", "1700000000", "1700000030", "15s");

        result.Success.Should().BeTrue();
        result.ResultType.Should().Be("matrix");
        result.Series.Should().HaveCount(1);
        result.Series[0].DataPoints.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryRangeAsync_EmptyResult_ReturnsEmptySeries()
    {
        var prometheusResponse = new
        {
            status = "success",
            data = new { resultType = "matrix", result = Array.Empty<object>() },
        };
        var service = CreateService(SuccessHandler(prometheusResponse));

        var result = await service.QueryRangeAsync("nonexistent_metric", "1700000000", "1700003600", "1m");

        result.Success.Should().BeTrue();
        result.Series.Should().BeEmpty();
    }

    // --- GetHealthAsync ---

    [Fact]
    public async Task GetHealthAsync_PrometheusUp_ReturnsHealthyWithVersion()
    {
        var buildInfo = new
        {
            status = "success",
            data = new { version = "2.53.0", revision = "abc123" },
        };
        var service = CreateService(SuccessHandler(buildInfo));

        var result = await service.GetHealthAsync();

        result.Healthy.Should().BeTrue();
        result.Version.Should().Be("2.53.0");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task GetHealthAsync_PrometheusDown_ReturnsUnhealthy()
    {
        var service = CreateService(new FakePrometheusHandler(new HttpRequestException("Connection refused")));

        var result = await service.GetHealthAsync();

        result.Healthy.Should().BeFalse();
        result.Error.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task GetHealthAsync_Non200_ReturnsUnhealthy()
    {
        var service = CreateService(ErrorHandler(HttpStatusCode.ServiceUnavailable));

        var result = await service.GetHealthAsync();

        result.Healthy.Should().BeFalse();
    }

    // --- Cancellation ---

    [Fact]
    public async Task QueryInstantAsync_Cancelled_ThrowsOperationCancelled()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = CreateService(SuccessHandler(new { status = "success", data = new { resultType = "vector", result = Array.Empty<object>() } }));

        var act = () => service.QueryInstantAsync("up", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

/// <summary>
/// Minimal fake <see cref="HttpMessageHandler"/> that returns a canned response
/// or throws a configured exception. Captures the last request URI for assertion.
/// </summary>
internal sealed class FakePrometheusHandler : HttpMessageHandler
{
    private readonly HttpStatusCode? _statusCode;
    private readonly string? _responseBody;
    private readonly Exception? _exception;

    public string? LastRequestUri { get; private set; }

    public FakePrometheusHandler(HttpStatusCode statusCode, string responseBody)
    {
        _statusCode = statusCode;
        _responseBody = responseBody;
    }

    public FakePrometheusHandler(Exception exception) => _exception = exception;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestUri = request.RequestUri?.ToString();

        if (_exception is not null)
            throw _exception;

        return Task.FromResult(new HttpResponseMessage(_statusCode!.Value)
        {
            Content = new StringContent(_responseBody ?? string.Empty),
        });
    }
}

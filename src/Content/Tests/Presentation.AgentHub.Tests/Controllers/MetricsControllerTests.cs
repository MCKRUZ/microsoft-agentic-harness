using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

public sealed class MetricsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MetricsControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateAuthedClient(
        Action<IServiceCollection>? configureServices = null)
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.ConfigureTestServices(services =>
                {
                    services.AddAuthentication(TestAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            TestAuthHandler.SchemeName, _ => { });
                    configureServices?.Invoke(services);
                });
            })
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "metrics-user");
        return client;
    }

    private static Mock<IPrometheusQueryService> CreateMockPrometheus(
        MetricsQueryResponse? instantResponse = null,
        MetricsQueryResponse? rangeResponse = null,
        PrometheusHealthResponse? healthResponse = null)
    {
        var mock = new Mock<IPrometheusQueryService>();

        mock.Setup(x => x.QueryInstantAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instantResponse ?? new MetricsQueryResponse { Success = true, ResultType = "vector", Series = [] });

        mock.Setup(x => x.QueryRangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rangeResponse ?? new MetricsQueryResponse { Success = true, ResultType = "matrix", Series = [] });

        mock.Setup(x => x.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthResponse ?? new PrometheusHealthResponse { Healthy = true, Version = "2.53.0" });

        return mock;
    }

    // --- Instant Query ---

    [Fact]
    public async Task QueryInstant_ValidQuery_Returns200()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/instant?query=up");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueryInstant_ValidQuery_ReturnsNormalizedSeries()
    {
        var expected = new MetricsQueryResponse
        {
            Success = true,
            ResultType = "vector",
            Series =
            [
                new MetricSeries
                {
                    Labels = new Dictionary<string, string> { ["__name__"] = "up" },
                    DataPoints = [new MetricDataPoint { Timestamp = 1700000000, Value = "1" }],
                },
            ],
        };
        var mock = CreateMockPrometheus(instantResponse: expected);
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/instant?query=up");
        var result = await response.Content.ReadFromJsonAsync<MetricsQueryResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Series.Should().HaveCount(1);
        result.Series[0].DataPoints.Should().HaveCount(1);
    }

    [Fact]
    public async Task QueryInstant_MissingQuery_Returns400()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/instant?query=");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryInstant_EmptyQueryParam_Returns400()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/instant");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("up; rm -rf /")]
    [InlineData("up && echo pwned")]
    [InlineData("up || true")]
    [InlineData("up `whoami`")]
    [InlineData("up $(id)")]
    public async Task QueryInstant_InjectionAttempt_Returns400(string maliciousQuery)
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync($"/api/metrics/instant?query={Uri.EscapeDataString(maliciousQuery)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryInstant_PrometheusError_Returns502()
    {
        var errorResponse = new MetricsQueryResponse { Success = false, Error = "bad_data: invalid expression" };
        var mock = CreateMockPrometheus(instantResponse: errorResponse);
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/instant?query=invalid{");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // --- Range Query ---

    [Fact]
    public async Task QueryRange_ValidParams_Returns200()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync(
            "/api/metrics/range?query=up&start=1700000000&end=1700003600&step=15s");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueryRange_MissingStart_Returns400()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync(
            "/api/metrics/range?query=up&end=1700003600&step=15s");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryRange_MissingStep_Returns400()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync(
            "/api/metrics/range?query=up&start=1700000000&end=1700003600");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryRange_MissingQuery_Returns400()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync(
            "/api/metrics/range?start=1700000000&end=1700003600&step=15s");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Catalog ---

    [Fact]
    public async Task GetCatalog_Returns200WithEntries()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/catalog");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await response.Content.ReadFromJsonAsync<List<MetricCatalogEntry>>();
        entries.Should().NotBeNull();
        entries!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetCatalog_AllEntriesHaveRequiredFields()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/catalog");
        var entries = await response.Content.ReadFromJsonAsync<List<MetricCatalogEntry>>();

        foreach (var entry in entries!)
        {
            entry.Id.Should().NotBeNullOrWhiteSpace();
            entry.Title.Should().NotBeNullOrWhiteSpace();
            entry.Query.Should().NotBeNullOrWhiteSpace();
            entry.Category.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task GetCatalog_ContainsExpectedCategories()
    {
        var mock = CreateMockPrometheus();
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/catalog");
        var entries = await response.Content.ReadFromJsonAsync<List<MetricCatalogEntry>>();

        var categories = entries!.Select(e => e.Category).Distinct().ToList();
        categories.Should().Contain("overview");
        categories.Should().Contain("tokens");
        categories.Should().Contain("cost");
        categories.Should().Contain("tools");
        categories.Should().Contain("safety");
    }

    // --- Health ---

    [Fact]
    public async Task GetHealth_WhenHealthy_Returns200()
    {
        var mock = CreateMockPrometheus(
            healthResponse: new PrometheusHealthResponse { Healthy = true, Version = "2.53.0" });
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PrometheusHealthResponse>();
        result!.Healthy.Should().BeTrue();
        result.Version.Should().Be("2.53.0");
    }

    [Fact]
    public async Task GetHealth_WhenUnhealthy_Returns503()
    {
        var mock = CreateMockPrometheus(
            healthResponse: new PrometheusHealthResponse { Healthy = false, Error = "Connection refused" });
        using var client = CreateAuthedClient(s =>
            s.AddSingleton<IPrometheusQueryService>(mock.Object));

        var response = await client.GetAsync("/api/metrics/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // --- Auth ---

    [Fact]
    public async Task QueryInstant_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/metrics/instant?query=up");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCatalog_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/metrics/catalog");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

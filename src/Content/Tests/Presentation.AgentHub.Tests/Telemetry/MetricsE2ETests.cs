using System.Net;
using System.Net.Http.Json;
using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Tests.Telemetry.Fixtures;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// End-to-end tests that validate the full observability pipeline from metric emission
/// through to the Dashboard-facing API endpoints:
/// App (metric emission) → OTel Collector → Prometheus → MetricsController → Dashboard response.
/// </summary>
/// <remarks>
/// <para>
/// These tests require Docker. They spin up real OTel Collector and Prometheus containers
/// via Testcontainers, configure the app to export OTLP to the collector AND point
/// <see cref="Presentation.AgentHub.Services.PrometheusQueryService"/> at the test Prometheus
/// instance. This means <c>/api/metrics/*</c> endpoints return real data from the E2E pipeline.
/// </para>
/// <para>
/// Filter with <c>dotnet test --filter Category=E2E</c> to run only these tests,
/// or exclude them with <c>--filter Category!=E2E</c> in environments without Docker.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "Telemetry")]
public class MetricsE2ETests : IClassFixture<PrometheusFixture>, IAsyncLifetime
{
    private readonly PrometheusFixture _infra;
    private MetricsE2EFactory _factory = null!;
    private HttpClient _client = null!;

    public MetricsE2ETests(PrometheusFixture infra) => _infra = infra;

    public Task InitializeAsync()
    {
        _factory = new MetricsE2EFactory(
            _infra.CollectorOtlpGrpcEndpoint,
            _infra.PrometheusQueryEndpoint);

        // Force host creation so the app starts and DI is built.
        _ = _factory.Services;

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "e2e-test-user");

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Layer 4a: Pipeline verification (metrics reach Prometheus)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends a chat through the real MediatR pipeline, waits for the
    /// <c>agent_session_started_total</c> metric to appear in Prometheus
    /// (namespaced as <c>agentic_harness_agent_session_started_total</c> by the collector).
    /// </summary>
    [Fact]
    public async Task FullPipeline_ChatProducesPrometheusData()
    {
        await SendChatAsync("e2e-session-agent", "Hello from E2E test");

        var found = await _infra.WaitForMetric(
            "agentic_harness_agent_session_started_total",
            TimeSpan.FromSeconds(30));

        found.Should().BeTrue(
            "agent_session_started_total should propagate through collector → Prometheus within 30s");
    }

    /// <summary>
    /// Verifies that orchestration turn metrics reach Prometheus through the full pipeline.
    /// </summary>
    [Fact]
    public async Task FullPipeline_OrchestrationMetricsReachPrometheus()
    {
        await SendChatAsync("e2e-orchestration-agent", "Orchestration E2E check");

        var found = await _infra.WaitForMetric(
            "agentic_harness_agent_orchestration_turns_total",
            TimeSpan.FromSeconds(30));

        found.Should().BeTrue(
            "agent_orchestration_turns_total should propagate through collector → Prometheus within 30s");
    }

    /// <summary>
    /// Asserts that no double-prefix bug exists in the Prometheus data.
    /// The collector config adds <c>agentic_harness_</c> namespace, so if the app
    /// also prefixed metrics, we'd see <c>agentic_harness_agentic_harness_*</c>.
    /// </summary>
    [Fact]
    public async Task FullPipeline_NoDoublePrefixInPrometheus()
    {
        await SendChatAsync("e2e-prefix-agent", "Double prefix check");

        await _infra.WaitForMetric(
            "agentic_harness_agent_session_started_total",
            TimeSpan.FromSeconds(30));

        var doublePrefix = await _infra.QueryPrometheus(
            "{__name__=~\"agentic_harness_agentic_harness_.*\"}");

        doublePrefix.Should().BeNull(
            "metrics must NOT have double 'agentic_harness_' prefix — " +
            "the app emits unprefixed names, the collector namespace handles prefixing");
    }

    /// <summary>
    /// Verifies the collector's <c>filter/app_only</c> processor accepts traffic from
    /// our app (service.name=Presentation.AgentHub).
    /// </summary>
    [Fact]
    public async Task FullPipeline_CollectorFilterAcceptsAppTraffic()
    {
        await SendChatAsync("e2e-filter-agent", "Filter acceptance check");

        var found = await _infra.WaitForMetric(
            "{__name__=~\"agentic_harness_agent_.*\"}",
            TimeSpan.FromSeconds(30));

        found.Should().BeTrue(
            "collector filter/app_only should accept traffic from service.name=Presentation.AgentHub");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Layer 4b: Controller proxy verification (Dashboard → MetricsController → Prometheus)
    // These test the exact code path the Dashboard SPA uses.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// After metrics propagate, calls <c>/api/metrics/instant</c> — the same endpoint
    /// the Dashboard calls — and asserts it returns non-empty series from the real
    /// Prometheus. This is the test that would have caught "Dashboard shows nothing."
    /// </summary>
    [Fact]
    public async Task MetricsController_InstantQueryReturnsRealData()
    {
        await SendChatAsync("e2e-controller-agent", "Controller proxy test");

        await _infra.WaitForMetric(
            "agentic_harness_agent_session_started_total",
            TimeSpan.FromSeconds(30));

        var response = await _client.GetAsync(
            "/api/metrics/instant?query=agentic_harness_agent_session_started_total");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "MetricsController should proxy the query to the test Prometheus instance");

        var result = await response.Content.ReadFromJsonAsync<MetricsQueryResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue("the Prometheus query should succeed");
        result.Series.Should().NotBeEmpty(
            "the Dashboard calls /api/metrics/instant — if this returns empty series, " +
            "the Dashboard shows nothing. This is the exact code path that was untested.");
    }

    /// <summary>
    /// Calls <c>/api/metrics/range</c> with a time window covering the test run.
    /// The Dashboard's <c>usePromQuery</c> hook calls this endpoint for all time-series panels.
    /// </summary>
    [Fact]
    public async Task MetricsController_RangeQueryReturnsTimeSeries()
    {
        await SendChatAsync("e2e-range-agent", "Range query test");

        await _infra.WaitForMetric(
            "agentic_harness_agent_session_started_total",
            TimeSpan.FromSeconds(30));

        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-5).ToUnixTimeSeconds();
        var end = now.ToUnixTimeSeconds();

        var response = await _client.GetAsync(
            $"/api/metrics/range?query=agentic_harness_agent_session_started_total" +
            $"&start={start}&end={end}&step=15s");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<MetricsQueryResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Series.Should().NotBeEmpty(
            "range query should return time-series data — " +
            "the Dashboard's usePromQuery hook relies on this endpoint for all chart panels");
    }

    /// <summary>
    /// Calls <c>/api/metrics/health</c> to verify the Prometheus health check works
    /// through the controller proxy. The Dashboard shows a connection status indicator.
    /// </summary>
    [Fact]
    public async Task MetricsController_HealthEndpointReportsHealthy()
    {
        var response = await _client.GetAsync("/api/metrics/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PrometheusHealthResponse>();

        result.Should().NotBeNull();
        result!.Healthy.Should().BeTrue(
            "PrometheusQueryService should reach the Testcontainers Prometheus instance — " +
            "if this fails, the Dashboard shows 'Backend unreachable'");
    }

    /// <summary>
    /// Calls <c>/api/metrics/catalog</c> to verify the panel catalog endpoint.
    /// The Dashboard fetches this to know which panels to render.
    /// </summary>
    [Fact]
    public async Task MetricsController_CatalogEndpointReturnsPanels()
    {
        var response = await _client.GetAsync("/api/metrics/catalog");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<MetricCatalogEntry[]>();

        result.Should().NotBeNull();
        result!.Should().NotBeEmpty("the catalog should contain panel definitions for the Dashboard");
        result.Should().Contain(e => e.Category == "overview",
            "at least one overview panel should exist");
    }

    /// <summary>
    /// Exercises the full Dashboard data path for a PromQL query that uses
    /// <c>rate()</c> and <c>or vector(0)</c> — the exact pattern used by the
    /// Pulse page's "Tokens / Minute" KPI card.
    /// </summary>
    [Fact]
    public async Task MetricsController_DashboardStyleQueryWorks()
    {
        await SendChatAsync("e2e-dashboard-agent", "Dashboard query pattern test");

        await _infra.WaitForMetric(
            "agentic_harness_agent_session_started_total",
            TimeSpan.FromSeconds(30));

        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-5).ToUnixTimeSeconds();
        var end = now.ToUnixTimeSeconds();

        // This is the exact PromQL the Dashboard's Pulse page uses for active sessions.
        var query = "agentic_harness_agent_session_active or vector(0)";
        var response = await _client.GetAsync(
            $"/api/metrics/range?query={Uri.EscapeDataString(query)}" +
            $"&start={start}&end={end}&step=15s");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<MetricsQueryResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Series.Should().NotBeEmpty(
            "the Dashboard's Pulse page queries 'agent_session_active or vector(0)' via " +
            "/api/metrics/range — this must return data for the page to render");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task SendChatAsync(string agentName, string message)
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = agentName,
            UserMessages = [message],
            MaxTurns = 1,
        };

        var result = await mediator.Send(command, CancellationToken.None);
        result.Success.Should().BeTrue("stub agent factory should produce a valid response");

        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();
    }
}

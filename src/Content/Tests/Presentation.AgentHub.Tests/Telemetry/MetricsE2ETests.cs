using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Presentation.AgentHub.Tests.Telemetry.Fixtures;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// End-to-end tests that validate the full observability pipeline:
/// Application (metric emission) → OTel Collector (filtering, namespace prefixing) → Prometheus (storage + query).
/// </summary>
/// <remarks>
/// <para>
/// These tests require Docker to be running. They spin up real OTel Collector and Prometheus
/// containers via Testcontainers, configure the app to export OTLP to the collector,
/// send a real chat via MediatR, then query Prometheus HTTP API to verify metrics propagated.
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

    public MetricsE2ETests(PrometheusFixture infra) => _infra = infra;

    public Task InitializeAsync()
    {
        _factory = new MetricsE2EFactory(_infra.CollectorOtlpGrpcEndpoint);
        // Force host creation so the app starts and DI is built.
        _ = _factory.Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
    }

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

        // Wait for valid metrics to arrive first so we know the pipeline is warm.
        await _infra.WaitForMetric(
            "agentic_harness_agent_session_started_total",
            TimeSpan.FromSeconds(30));

        // Now query for the double-prefix pattern — should NOT exist.
        var doublePrefix = await _infra.QueryPrometheus(
            "{__name__=~\"agentic_harness_agentic_harness_.*\"}");

        doublePrefix.Should().BeNull(
            "metrics must NOT have double 'agentic_harness_' prefix — " +
            "the app emits unprefixed names, the collector namespace handles prefixing");
    }

    /// <summary>
    /// Verifies the collector's <c>filter/app_only</c> processor accepts traffic from
    /// our app (service.name=Presentation.AgentHub) by checking that any
    /// <c>agentic_harness_agent_*</c> metric exists in Prometheus.
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

        // Flush the MeterProvider to push buffered metrics to the OTLP exporter.
        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();
    }
}

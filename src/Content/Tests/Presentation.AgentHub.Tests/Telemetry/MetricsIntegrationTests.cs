using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Presentation.AgentHub.Tests.Telemetry.Fixtures;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// Integration tests that exercise the real MediatR handler pipeline end-to-end,
/// then scrape the Prometheus <c>/metrics</c> endpoint to verify that orchestration,
/// session, and token metrics were emitted by the handlers — not just statically
/// registered. Also asserts that no double-prefix bug exists.
/// </summary>
[Trait("Category", "Telemetry")]
[Trait("Category", "Integration")]
public class MetricsIntegrationTests : IClassFixture<MetricsIntegrationFactory>
{
    private readonly MetricsIntegrationFactory _factory;

    public MetricsIntegrationTests(MetricsIntegrationFactory factory) => _factory = factory;

    /// <summary>
    /// Sends a <see cref="RunConversationCommand"/> through the real MediatR pipeline,
    /// which dispatches <c>ExecuteAgentTurnCommand</c> internally, triggering metric
    /// emission in both handlers. Then scrapes <c>/metrics</c> to verify presence.
    /// </summary>
    [Fact]
    public async Task RunConversation_EmitsSessionAndOrchestrationMetrics()
    {
        // Arrange — resolve real IMediator from the DI container
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "metrics-test-agent",
            UserMessages = ["Hello from the integration test"],
            MaxTurns = 1,
        };

        // Act ��� execute a real conversation turn
        var result = await mediator.Send(command, CancellationToken.None);

        // Assert — command succeeded
        result.Success.Should().BeTrue("the stub agent factory should return a valid response");

        // Flush metrics and scrape the Prometheus endpoint
        var metrics = await ScrapeMetricsAsync();

        // Session metrics (emitted by RunConversationCommandHandler)
        metrics.Should().Contain("agent_session_started_total",
            "RunConversationCommandHandler emits SessionMetrics.SessionsStarted");

        // Orchestration metrics (emitted by both handlers)
        metrics.Should().Contain("agent_orchestration_turns_total",
            "ExecuteAgentTurnCommandHandler emits OrchestrationMetrics.TurnsTotal");
        metrics.Should().Contain("agent_orchestration_turn_duration",
            "ExecuteAgentTurnCommandHandler emits OrchestrationMetrics.TurnDuration");
        metrics.Should().Contain("agent_orchestration_conversation_duration",
            "RunConversationCommandHandler emits OrchestrationMetrics.ConversationDuration");
        metrics.Should().Contain("agent_orchestration_turns_per_conversation",
            "RunConversationCommandHandler emits OrchestrationMetrics.TurnsPerConversation");
    }

    [Fact]
    public async Task RunConversation_EmitsAgentNameTag()
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "tagged-agent",
            UserMessages = ["Tag verification"],
            MaxTurns = 1,
        };

        await mediator.Send(command, CancellationToken.None);

        var metrics = await ScrapeMetricsAsync();

        // The agent_name tag should appear for our agent or any agent emitted by this fixture.
        // All RunConversationCommand dispatches emit metrics with the agent_name dimension.
        metrics.Should().MatchRegex("agent_name=\"[^\"]+\"",
            "metrics emitted by the handler should include the agent_name tag");
    }

    [Fact]
    public async Task RunConversation_EmitsSafetyEvaluationMetric()
    {
        // ExecuteAgentTurnCommand implements IContentScreenable, so the
        // ContentSafetyBehavior pipeline behavior should emit a safety metric.
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "safety-test-agent",
            UserMessages = ["Test content for safety screening"],
            MaxTurns = 1,
        };

        await mediator.Send(command, CancellationToken.None);

        var metrics = await ScrapeMetricsAsync();

        metrics.Should().Contain("agent_safety_evaluations_total",
            "ContentSafetyBehavior emits ContentSafetyMetrics.Evaluations on every turn");
    }

    [Fact]
    public async Task RunConversation_NoDoublePrefix()
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "prefix-test-agent",
            UserMessages = ["Prefix check"],
            MaxTurns = 1,
        };

        await mediator.Send(command, CancellationToken.None);

        var metrics = await ScrapeMetricsAsync();

        // The double-prefix bug would produce "agentic_harness_agentic_harness_*"
        // or "agentic_harness_agent_*" in the TYPE lines.
        var typeLines = metrics.Split('\n')
            .Where(l => l.StartsWith("# TYPE"))
            .ToList();

        typeLines.Should().NotContain(l => l.Contains("agentic_harness_"),
            "metric names must NOT contain 'agentic_harness_' prefix — " +
            "the otel-collector namespace handles prefixing, not the app");
    }

    [Fact]
    public async Task RunConversation_MetricsHaveNonZeroValues()
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "nonzero-test-agent",
            UserMessages = ["Ensure non-zero values"],
            MaxTurns = 1,
        };

        await mediator.Send(command, CancellationToken.None);

        var metrics = await ScrapeMetricsAsync();

        // At least one counter should have a value > 0
        var counterLines = metrics.Split('\n')
            .Where(l => !l.StartsWith('#') && l.Contains("_total{"))
            .ToList();

        counterLines.Should().NotBeEmpty("handler execution should produce at least one counter metric");

        var nonZero = counterLines.Where(l =>
        {
            var parts = l.Split(' ');
            return parts.Length >= 2 && double.TryParse(parts[1], out var val) && val > 0;
        });

        nonZero.Should().NotBeEmpty(
            "counters should have values > 0 after executing a real agent turn");
    }

    [Fact]
    public async Task PrometheusEndpoint_ReturnsSuccessAfterIntegrationRun()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "metrics-integration");

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> ScrapeMetricsAsync()
    {
        // Force-flush the MeterProvider to ensure all recorded values are exported.
        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "metrics-integration");

        var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

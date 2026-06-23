using System.Diagnostics.Metrics;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.Common.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// E2E tests verifying the full telemetry pipeline: instrument creation → OTel SDK collection
/// → Prometheus exporter → /metrics scrape endpoint. Catches regressions like missing meter
/// registration, namespace removal, instrument naming changes, and DI wiring failures.
/// </summary>
[Trait("Category", "Telemetry")]
public class TelemetryPipelineTests : IClassFixture<TelemetryPipelineTests.TelemetryFactory>
{
    private readonly TelemetryFactory _factory;

    public TelemetryPipelineTests(TelemetryFactory factory) => _factory = factory;

    /// <summary>
    /// Custom factory that registers <see cref="AppInstrument.Meter"/> in the OTel metrics
    /// pipeline so all static instruments are collected and exported to Prometheus.
    /// </summary>
    public class TelemetryFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<MeterProvider>();
                services.AddOpenTelemetry()
                    .WithMetrics(m =>
                    {
                        m.AddMeter(AppSourceNames.AgenticHarness);
                        m.AddPrometheusExporter();
                    });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
            });
        }
    }

    private async Task<string> ScrapeMetrics()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "telemetry-test");

        EmitAllMetrics();

        // Force a metrics flush by collecting
        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();

        var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static void EmitAllMetrics()
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("agent.name", "test-agent"),
            new("model", "gpt-4o"),
        };

        // Session metrics
        SessionMetrics.ActiveSessions.Add(1, tags);
        SessionMetrics.SessionsStarted.Add(1, tags);
        SessionMetrics.SessionCost.Record(0.05, tags);

        // Orchestration metrics
        OrchestrationMetrics.ConversationDuration.Record(1500.0, tags);
        OrchestrationMetrics.TurnsPerConversation.Record(3, tags);
        OrchestrationMetrics.SubagentSpawns.Add(1, tags);
        OrchestrationMetrics.ToolCalls.Add(2, tags);
        OrchestrationMetrics.TurnDuration.Record(500.0, tags);
        OrchestrationMetrics.TurnsTotal.Add(1, tags);
        OrchestrationMetrics.TurnErrors.Add(1, tags);

        // Token usage metrics
        TokenUsageMetrics.InputTokens.Record(100, tags);
        TokenUsageMetrics.OutputTokens.Record(50, tags);
        TokenUsageMetrics.TotalTokens.Record(150, tags);
        TokenUsageMetrics.BudgetUsed.Add(150, tags);

        // Content safety metrics
        ContentSafetyMetrics.Evaluations.Add(1, tags);
        ContentSafetyMetrics.Blocks.Add(1, tags);
        ContentSafetyMetrics.Severity.Record(2, tags);
        ContentSafetyMetrics.Flags.Add(1, tags);
        ContentSafetyMetrics.Redactions.Add(1, tags);

        // Tool execution metrics
        var toolTags = new KeyValuePair<string, object?>[]
        {
            new("agent.tool.name", "file_read"),
            new("agent.tool.source", "keyed_di"),
            new("agent.tool.status", "success"),
        };
        ToolExecutionMetrics.Duration.Record(42.0, toolTags);
        ToolExecutionMetrics.Invocations.Add(1, toolTags);
        ToolExecutionMetrics.Errors.Add(1, toolTags);
        ToolExecutionMetrics.EmptyResults.Add(1, toolTags);
        ToolExecutionMetrics.ResultSize.Record(256, toolTags);

        // RAG retrieval metrics
        RagRetrievalMetrics.RetrievalDuration.Record(120.0);
        RagRetrievalMetrics.ChunksReturned.Record(5);
        RagRetrievalMetrics.RerankDuration.Record(30.0);
        RagRetrievalMetrics.Queries.Add(1);
        RagRetrievalMetrics.Errors.Add(1);
        RagRetrievalMetrics.Hits.Add(1);
        RagRetrievalMetrics.SourceRetrievals.Add(1);
        RagRetrievalMetrics.GroundingScore.Record(0.85);

        // Governance metrics
        GovernanceMetrics.Decisions.Add(1);
        GovernanceMetrics.Violations.Add(1);
        GovernanceMetrics.EvaluationDuration.Record(5.0);
        GovernanceMetrics.RateLimitHits.Add(1);
        GovernanceMetrics.AuditEvents.Add(1);
        GovernanceMetrics.InjectionDetections.Add(1);
        GovernanceMetrics.McpScans.Add(1);
        GovernanceMetrics.McpThreats.Add(1);

        // Context budget metrics
        ContextBudgetMetrics.Compactions.Add(1, tags);
        ContextBudgetMetrics.SystemPromptTokens.Record(200, tags);
        ContextBudgetMetrics.SkillsLoadedTokens.Record(300, tags);
    }

    /// <summary>
    /// Maps OTel metric names (dot-separated) to the Prometheus-exported names.
    /// Prometheus converts dots to underscores. Counters get _total suffix (deduplicated
    /// if the name already ends with _total). Histograms get _bucket/_sum/_count suffixes.
    /// UpDownCounters have no suffix. Bare units (e.g. "ms") are appended as full words
    /// (e.g. "_milliseconds") by the OTel Prometheus exporter; curly-brace units ("{ms}")
    /// are annotations only and NOT appended.
    /// </summary>
    private static string ToPrometheusName(string baseName, string type)
    {
        return type switch
        {
            "counter" => baseName.EndsWith("_total") ? baseName : baseName + "_total",
            "histogram_sum" => baseName + "_sum",
            "histogram_count" => baseName + "_count",
            "histogram_bucket" => baseName + "_bucket",
            "updowncounter" => baseName,
            _ => baseName,
        };
    }

    [Fact]
    public async Task PrometheusEndpoint_Returns200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/metrics");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task AllSessionMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("agent_session_active", "updowncounter"));
        metrics.Should().Contain(ToPrometheusName("agent_session_started", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_session_cost", "histogram_sum"));
    }

    [Fact]
    public async Task AllOrchestrationMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("agent_orchestration_conversation_duration", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_orchestration_turns_per_conversation", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_orchestration_subagent_spawns", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_orchestration_tool_call_count", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_orchestration_turn_duration", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_orchestration_turns_total", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_orchestration_turn_errors", "counter"));
    }

    [Fact]
    public async Task AllTokenMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("agent_tokens_input", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_tokens_output", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_tokens_total", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_tokens_budget_used", "updowncounter"));
    }

    [Fact]
    public async Task AllSafetyMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("agent_safety_evaluations", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_safety_blocks", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_safety_severity", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_safety_flags", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_safety_redactions", "counter"));
    }

    [Fact]
    public async Task AllToolMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("agent_tool_duration", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_tool_invocations", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_tool_errors", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_tool_empty_results", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_tool_result_size", "histogram_sum"));
    }

    [Fact]
    public async Task AllRagMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("rag_retrieval_duration", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("rag_retrieval_chunks_returned", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("rag_rerank_duration", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("rag_retrieval_queries", "counter"));
        metrics.Should().Contain(ToPrometheusName("rag_retrieval_errors", "counter"));
        metrics.Should().Contain(ToPrometheusName("rag_retrieval_hits", "counter"));
        metrics.Should().Contain(ToPrometheusName("rag_source_retrievals", "counter"));
        metrics.Should().Contain(ToPrometheusName("rag_grounding_score", "histogram_sum"));
    }

    [Fact]
    public async Task AllGovernanceMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("agent_governance_decisions", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_governance_violations", "counter"));
        // Unit "ms" (not "{ms}") causes OTel exporter to append "_milliseconds"
        metrics.Should().Contain(ToPrometheusName("agent_governance_evaluation_duration_milliseconds", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_governance_rate_limit_hits", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_governance_audit_events", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_governance_injection_detections", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_governance_mcp_scans", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_governance_mcp_threats", "counter"));
    }

    [Fact]
    public async Task AllContextBudgetMetrics_AppearInPrometheusOutput()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain(ToPrometheusName("agent_context_compactions", "counter"));
        metrics.Should().Contain(ToPrometheusName("agent_context_system_prompt_tokens", "histogram_sum"));
        metrics.Should().Contain(ToPrometheusName("agent_context_skills_loaded_tokens", "histogram_sum"));
    }

    [Fact]
    public async Task PrometheusOutput_ContainsAgentNameTag()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain("agent_name=\"test-agent\"",
            "dashboard queries group by agent_name — the tag must be present");
    }

    [Fact]
    public async Task PrometheusOutput_ContainsModelTag()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain("model=\"gpt-4o\"",
            "dashboard cost/token-by-model queries group by the short `model` label");
    }

    [Fact]
    public async Task PrometheusOutput_ContainsToolNameTag()
    {
        var metrics = await ScrapeMetrics();

        metrics.Should().Contain("agent_tool_name=\"file_read\"",
            "dashboard tool queries group by agent_tool_name");
    }

    [Fact]
    public async Task CounterMetrics_HaveNonZeroValues()
    {
        var metrics = await ScrapeMetrics();

        // Verify counters have values > 0 — proves data flows, not just metric registration
        var lines = metrics.Split('\n')
            .Where(l => !l.StartsWith('#') && l.Contains("_total{"))
            .ToList();

        lines.Should().NotBeEmpty("at least one counter should have a non-zero value");

        var nonZeroLines = lines.Where(l =>
        {
            var parts = l.Split(' ');
            return parts.Length >= 2 && double.TryParse(parts[1], out var val) && val > 0;
        });

        nonZeroLines.Should().NotBeEmpty(
            "counters should have values > 0 after EmitAllMetrics records data");
    }

    [Fact]
    public async Task HistogramMetrics_HaveNonZeroCounts()
    {
        var metrics = await ScrapeMetrics();

        var countLines = metrics.Split('\n')
            .Where(l => !l.StartsWith('#') && l.Contains("_count{"))
            .ToList();

        countLines.Should().NotBeEmpty("at least one histogram should have count > 0");

        var nonZero = countLines.Where(l =>
        {
            var parts = l.Split(' ');
            return parts.Length >= 2 && double.TryParse(parts[1], out var val) && val > 0;
        });

        nonZero.Should().NotBeEmpty(
            "histogram _count should be > 0 after recording values");
    }

    [Fact]
    public async Task MeterName_IsAgenticHarness()
    {
        // The OTel SDK exports metrics grouped by meter name in the HELP/TYPE comments.
        // Verifying this proves the correct meter is wired up.
        var metrics = await ScrapeMetrics();

        // At minimum, our instruments should produce TYPE lines
        var typeLines = metrics.Split('\n')
            .Where(l => l.StartsWith("# TYPE"))
            .ToList();

        typeLines.Should().NotBeEmpty("Prometheus output should contain TYPE metadata lines");

        // Verify at least one of our known metrics appears in TYPE lines
        typeLines.Should().Contain(l => l.Contains("agent_session"),
            "agent_session metrics should be registered under the AgenticHarness meter");
    }

    [Fact]
    public async Task MetricNames_NotDoublePrefixed()
    {
        // Regression: an OTel View once renamed metrics to "agentic_harness.{name}" inside
        // the app. The otel-collector then added namespace "agentic_harness", producing
        // "agentic_harness_agentic_harness_*" — breaking every dashboard query.
        // This test catches any re-introduction of in-app prefixing.
        var metrics = await ScrapeMetrics();

        var typeLines = metrics.Split('\n')
            .Where(l => l.StartsWith("# TYPE"))
            .ToList();

        typeLines.Should().NotContain(l => l.Contains("agentic_harness_"),
            "metric names must NOT contain 'agentic_harness_' prefix — " +
            "the otel-collector namespace handles prefixing, not the app");
    }
}

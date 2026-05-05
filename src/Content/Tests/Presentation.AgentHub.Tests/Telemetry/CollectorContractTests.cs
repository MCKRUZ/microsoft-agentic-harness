using FluentAssertions;
using Presentation.AgentHub.Tests.Telemetry.Contracts;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// Validates that the OTel collector configuration and metric naming transforms
/// produce valid Prometheus-compatible metric names. These tests act as a contract
/// between the application instrumentation and the collector/dashboard layers.
/// </summary>
[Trait("Category", "Contract")]
public sealed class CollectorContractTests
{
    private static readonly string CollectorConfigPath =
        Path.Combine(GetRepoRoot(), "scripts", "otel-collector", "config.yaml");

    private static readonly string Namespace =
        MetricNamingContract.GetCollectorNamespace(CollectorConfigPath);

    [Fact]
    public void CollectorConfig_HasExpectedNamespace()
    {
        Namespace.Should().Be("agentic_harness",
            "the Prometheus exporter namespace must match what dashboards expect");
    }

    [Fact]
    public void AllInstruments_ProduceValidPrometheusNames()
    {
        var allNames = MetricNamingContract.GetAllExpectedPrometheusNames(Namespace);

        allNames.Should().AllSatisfy(name =>
        {
            name.Should().NotContain(".", "dots are not valid in Prometheus metric names");
            name.Should().NotContain("__", "double underscores are reserved in Prometheus");
        });
    }

    [Fact]
    public void CounterInstruments_GetTotalSuffix()
    {
        var counters = MetricNamingContract.AllInstruments
            .Where(i => i.Type == InstrumentType.Counter)
            .ToList();

        counters.Should().NotBeEmpty("contract must define at least one Counter instrument");

        foreach (var counter in counters)
        {
            var names = counter.ToAllPrometheusNames(Namespace);
            names.Should().HaveCount(1);
            names[0].Should().EndWith("_total",
                $"Counter '{counter.Name}' must produce a name ending in _total");
        }
    }

    [Fact]
    public void HistogramInstruments_GetSumCountBucket()
    {
        var histograms = MetricNamingContract.AllInstruments
            .Where(i => i.Type == InstrumentType.Histogram)
            .ToList();

        histograms.Should().NotBeEmpty("contract must define at least one Histogram instrument");

        foreach (var histogram in histograms)
        {
            var names = histogram.ToAllPrometheusNames(Namespace);
            names.Should().HaveCount(3,
                $"Histogram '{histogram.Name}' must produce _sum, _count, _bucket variants");
            names.Should().Contain(n => n.EndsWith("_sum"));
            names.Should().Contain(n => n.EndsWith("_count"));
            names.Should().Contain(n => n.EndsWith("_bucket"));
        }
    }

    [Fact]
    public void BareUnitMs_ProducesMillisecondsSuffix()
    {
        var governanceDuration = MetricNamingContract.AllInstruments
            .Single(i => i.Name == "agent.governance.evaluation_duration");

        governanceDuration.Unit.Should().Be("ms",
            "this instrument uses bare 'ms' unit (not curly-brace)");

        var names = governanceDuration.ToAllPrometheusNames(Namespace);

        names.Should().AllSatisfy(name =>
            name.Should().Contain("_milliseconds",
                "bare 'ms' unit must be expanded to '_milliseconds' in Prometheus names"));
    }

    [Fact]
    public void CurlyBraceUnits_ProduceNoUnitSuffix()
    {
        var sessionStarted = MetricNamingContract.AllInstruments
            .Single(i => i.Name == "agent.session.started");

        sessionStarted.Unit.Should().Be("{session}",
            "this instrument uses curly-brace unit annotation");

        var prometheusName = sessionStarted.ToPrometheusName(Namespace);

        // Curly-brace units are annotations only — the unit string should not appear as a suffix
        // (the base name naturally contains "session", so check the suffix after the base)
        var expectedBase = $"{Namespace}_agent_session_started_total";
        prometheusName.Should().Be(expectedBase,
            "curly-brace unit '{session}' must not add any suffix to the Prometheus name");
        prometheusName.Should().NotContain("{").And.NotContain("}");
    }

    [Fact]
    public void Snapshot_AllPrometheusNames_MatchesExpected()
    {
        var allNames = MetricNamingContract.GetAllExpectedPrometheusNamesSet(Namespace);

        // Spot-check critical metrics exist in the canonical set
        var criticalMetrics = new[]
        {
            "agentic_harness_agent_session_started_total",
            "agentic_harness_agent_session_active",
            "agentic_harness_agent_orchestration_turn_duration_sum",
            "agentic_harness_agent_tokens_input_sum",
            "agentic_harness_agent_safety_evaluations_total",
            "agentic_harness_rag_retrieval_duration_bucket",
            "agentic_harness_agent_tool_invocations_total",
            "agentic_harness_agent_governance_decisions_total",
            "agentic_harness_agent_governance_evaluation_duration_milliseconds_sum",
            "agentic_harness_agent_context_compactions_total",
        };

        foreach (var metric in criticalMetrics)
        {
            allNames.Should().Contain(metric,
                $"critical metric '{metric}' must exist in the naming contract");
        }
    }

    private static string GetRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}

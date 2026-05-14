using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Presentation.AgentHub.Tests.Telemetry.Contracts;

public static class MetricNamingContract
{
    public static readonly IReadOnlyList<InstrumentDefinition> AllInstruments = new[]
    {
        // SessionMetrics
        new InstrumentDefinition("agent.session.active", InstrumentType.UpDownCounter, "{session}"),
        new InstrumentDefinition("agent.session.cost", InstrumentType.Histogram, "{usd}"),
        new InstrumentDefinition("agent.session.started", InstrumentType.Counter, "{session}"),

        // OrchestrationMetrics
        new InstrumentDefinition("agent.orchestration.conversation_duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("agent.orchestration.turns_per_conversation", InstrumentType.Histogram, "{turn}"),
        new InstrumentDefinition("agent.orchestration.subagent_spawns", InstrumentType.Counter, "{spawn}"),
        new InstrumentDefinition("agent.orchestration.tool_call_count", InstrumentType.Counter, "{call}"),
        new InstrumentDefinition("agent.orchestration.turn_duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("agent.orchestration.turns_total", InstrumentType.Counter, "{turn}"),
        new InstrumentDefinition("agent.orchestration.turn_errors", InstrumentType.Counter),

        // TokenUsageMetrics
        new InstrumentDefinition("agent.tokens.input", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.tokens.output", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.tokens.total", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.tokens.budget_used", InstrumentType.UpDownCounter, "{token}"),
        new InstrumentDefinition("agent.tokens.cache_read", InstrumentType.Counter, "{token}"),
        new InstrumentDefinition("agent.tokens.cache_write", InstrumentType.Counter, "{token}"),
        new InstrumentDefinition("agent.tokens.cost_estimated", InstrumentType.Counter, "{usd}"),
        new InstrumentDefinition("agent.tokens.cost_cache_savings", InstrumentType.Counter, "{usd}"),
        new InstrumentDefinition("agent.tokens.cache_hit_rate", InstrumentType.Histogram, "{ratio}"),

        // ContentSafetyMetrics
        new InstrumentDefinition("agent.safety.evaluations", InstrumentType.Counter, "{evaluation}"),
        new InstrumentDefinition("agent.safety.blocks", InstrumentType.Counter, "{block}"),
        new InstrumentDefinition("agent.safety.severity", InstrumentType.Histogram, "{level}"),
        new InstrumentDefinition("agent.safety.flags", InstrumentType.Counter, "{flag}"),
        new InstrumentDefinition("agent.safety.redactions", InstrumentType.Counter, "{redaction}"),

        // RagRetrievalMetrics
        new InstrumentDefinition("rag.retrieval.duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("rag.retrieval.chunks_returned", InstrumentType.Histogram, "{chunk}"),
        new InstrumentDefinition("rag.rerank.duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("rag.retrieval.queries", InstrumentType.Counter, "{query}"),
        new InstrumentDefinition("rag.retrieval.errors", InstrumentType.Counter),
        new InstrumentDefinition("rag.retrieval.hits", InstrumentType.Counter),
        new InstrumentDefinition("rag.source_retrievals", InstrumentType.Counter),
        new InstrumentDefinition("rag.grounding_score", InstrumentType.Histogram),
        new InstrumentDefinition("rag.ingestion.documents", InstrumentType.Counter, "{document}"),

        // ToolExecutionMetrics
        new InstrumentDefinition("agent.tool.duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("agent.tool.invocations", InstrumentType.Counter, "{invocation}"),
        new InstrumentDefinition("agent.tool.errors", InstrumentType.Counter, "{error}"),
        new InstrumentDefinition("agent.tool.empty_results", InstrumentType.Counter, "{result}"),
        new InstrumentDefinition("agent.tool.result_size", InstrumentType.Histogram, "{char}"),

        // GovernanceMetrics
        new InstrumentDefinition("agent.governance.decisions", InstrumentType.Counter, "{decision}"),
        new InstrumentDefinition("agent.governance.violations", InstrumentType.Counter, "{violation}"),
        new InstrumentDefinition("agent.governance.evaluation_duration", InstrumentType.Histogram, "ms"),
        new InstrumentDefinition("agent.governance.rate_limit_hits", InstrumentType.Counter, "{hit}"),
        new InstrumentDefinition("agent.governance.audit_events", InstrumentType.Counter, "{event}"),
        new InstrumentDefinition("agent.governance.injection_detections", InstrumentType.Counter, "{detection}"),
        new InstrumentDefinition("agent.governance.mcp_scans", InstrumentType.Counter, "{scan}"),
        new InstrumentDefinition("agent.governance.mcp_threats", InstrumentType.Counter, "{threat}"),

        // ContextBudgetMetrics
        new InstrumentDefinition("agent.context.compactions", InstrumentType.Counter, "{compaction}"),
        new InstrumentDefinition("agent.context.system_prompt_tokens", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.context.skills_loaded_tokens", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.context.tools_schema_tokens", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.context.budget_utilization", InstrumentType.Histogram, "ratio"),

        // BudgetMetrics (ObservableGauges — registered via callbacks in BudgetTrackingService)
        new InstrumentDefinition("agent.budget.current_spend", InstrumentType.ObservableGauge),
        new InstrumentDefinition("agent.budget.status", InstrumentType.ObservableGauge),
        new InstrumentDefinition("agent.budget.threshold_warning", InstrumentType.ObservableGauge),
        new InstrumentDefinition("agent.budget.threshold_critical", InstrumentType.ObservableGauge),
    };

    public static string GetCollectorNamespace(string collectorConfigPath)
    {
        var yaml = File.ReadAllText(collectorConfigPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var config = deserializer.Deserialize<Dictionary<string, object>>(yaml);

        // Navigate: exporters -> prometheus -> namespace
        var exporters = (Dictionary<object, object>)config["exporters"];
        var prometheus = (Dictionary<object, object>)exporters["prometheus"];
        return prometheus["namespace"].ToString()!;
    }

    public static IReadOnlyList<string> GetAllExpectedPrometheusNames(string collectorNamespace)
    {
        return AllInstruments
            .SelectMany(i => i.ToAllPrometheusNames(collectorNamespace))
            .OrderBy(n => n)
            .ToList();
    }

    public static IReadOnlySet<string> GetAllExpectedPrometheusNamesSet(string collectorNamespace)
    {
        return AllInstruments
            .SelectMany(i => i.ToAllPrometheusNames(collectorNamespace))
            .ToHashSet();
    }
}

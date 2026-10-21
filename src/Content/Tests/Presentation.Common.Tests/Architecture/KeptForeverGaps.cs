namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// The stores that still declare <c>RetentionBasis.Unbounded</c>: nothing deletes what they hold (#901). Read by
/// <see cref="EveryPersonalDataStoreIsDeclaredTests"/>.
/// </summary>
/// <remarks>
/// This list only shrinks. A new store may not join it, and a store that gains a retention period, or a written reason to
/// be kept, must leave it, or the guard fails. Each entry says what will close the gap.
/// </remarks>
internal static class KeptForeverGaps
{
    /// <summary>The gaps, by full type name, with what closes each one.</summary>
    public static readonly IReadOnlyDictionary<string, string> Entries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // On by default.
        ["Infrastructure.AI.Prompts.Persistence.EfCorePromptUsageStore"] = "age-based sweep by RecordedAtUtc",
        ["Infrastructure.AI.Planner.EfCorePlanStateStore"] = "sweep of finished plans",
        ["Infrastructure.AI.Agents.JsonlDelegationStore"] = "sweep of closed delegation session files",
        ["Application.AI.Common.Factories.AgentFactory"] = "an expiry on every cached chat response",
        ["Infrastructure.AI.Runs.EfScheduleStore"] = "sweep of disabled schedules; enabled ones kept by design",
        ["Infrastructure.AI.Governance.EfCoreToolCallLedger"] = "rows leave with their conversation or run",

        // Knowledge graph and memory.
        ["Infrastructure.AI.RAG.GraphRag.KuzuGraphBackend"] = "enforce the expires_at it already writes",
        ["Infrastructure.AI.RAG.GraphRag.CrossSessionMemoryStore"] = "memory decay on by default",
        ["Infrastructure.AI.KnowledgeGraph.PostgreSql.PostgreSqlGraphStore"] = "a period for every entity type",
        ["Infrastructure.AI.KnowledgeGraph.Neo4j.Neo4jGraphStore"] = "a period for every entity type",
        ["Infrastructure.AI.KnowledgeGraph.Learnings.GraphLearningsStore"] = "pruned learnings removed, not only marked deleted",

        // Off by default, file-based, or external.
        ["Infrastructure.Observability.Persistence.PostgresObservabilityStore"] = "age-based sweep of closed sessions and their rows",
        ["Infrastructure.AI.Traces.FileSystemExecutionTraceStore"] = "sweep of completed trace runs",
        ["Application.Common.Logging.FileLoggerProvider"] = "sweep of old run log directories",
        ["Application.Common.Logging.StructuredJsonLoggerProvider"] = "sweep of old run log directories",
        ["Infrastructure.AI.StateManagement.Checkpoints.JsonCheckpointStateManager"] = "sweep of completed workflows",
        ["Infrastructure.AI.StateManagement.MarkdownCheckpointDecorator"] = "sweep of completed workflows",
        ["Infrastructure.AI.Changes.FileSystemEvidenceStore"] = "sweep of evidence no open proposal references",
        ["Infrastructure.AI.RAG.Retrieval.AzureAISearchVectorStore"] = "kept as long as its source document, or an ingest-time expiry",
        ["Infrastructure.AI.RAG.Retrieval.AzureAISearchBm25Store"] = "kept as long as its source document, or an ingest-time expiry",
    };
}

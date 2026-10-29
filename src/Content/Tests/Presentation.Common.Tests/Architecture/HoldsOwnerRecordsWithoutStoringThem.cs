namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Types that keep a collection of records naming a person without being a store of them, each with the reason and the
/// exact fields the scan saw when the reason was written (#990). Read by the holder rule in
/// <see cref="EveryPersonalDataStoreIsDeclaredTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the scan's own key, <c>Assembly/Namespace.Type</c>. An entry needs both: its records live no longer than the
/// live operation or session that uses them (one DI scope, an open connection that idles out within minutes), and they
/// repeat what a declared store already holds and erases. A type that keeps records past that, such as one held so a
/// caller can poll a finished result, is a store and must be declared, however short its retention.
/// </para>
/// <para>
/// When the guard reports an entry whose evidence changed, re-read the type before copying the new evidence in: the change
/// is exactly what the entry was not written to excuse.
/// </para>
/// </remarks>
internal static class HoldsOwnerRecordsWithoutStoringThem
{
    /// <summary>The entries, by scan key.</summary>
    public static readonly IReadOnlyDictionary<string, HoldsNoPersonalData.Entry> Entries =
        new Dictionary<string, HoldsNoPersonalData.Entry>(StringComparer.Ordinal)
        {
            ["Infrastructure.AI.KnowledgeGraph/Infrastructure.AI.KnowledgeGraph.Memory.InMemorySessionCache"] = new(
                "Scoped (AddScoped, and its one consumer KnowledgeMemoryService is scoped too): the graph nodes one request " +
                "produced, on their way to the knowledge graph, discarded when the DI scope ends. What it flushes is the " +
                "declared graph store's data, erased and exported with it.",
                "Infrastructure.AI.KnowledgeGraph.Memory.InMemorySessionCache._entries holds Domain.AI.KnowledgeGraph.Models.GraphNode.OwnerId"),
            ["Presentation.AgentHub/Presentation.AgentHub.Services.ConnectionTracker"] = new(
                "AgentHub presence: one entry per open SignalR connection, for the conversation it is on. ConversationId, " +
                "AgentName, UserId, ObservabilitySessionId and the running totals (turns, tool calls, tokens, cache reads " +
                "and writes, cost) repeat the conversation record, whose Telemetry and ObservabilitySessionId the " +
                "conversation store erases and exports, and the observability session where that is switched on. " +
                "StartedAt and LastActivityAt are this connection's own times and repeat no store. It holds no message, " +
                "tool argument or result (pinned field by field in ReviewedFieldByField). The entry goes when the " +
                "connection closes (HandleDisconnectAsync) or after AgentHub:SessionIdleTimeoutMinutes without a turn " +
                "(SessionIdleCleanupService; 5 by default, set by the operator, validated only as above 0). Ending one " +
                "after its person was erased sends the observability store an UPDATE that matches no row, so nothing is " +
                "re-created. Removing an entry outside those two paths would skip the active-connections gauge and leave " +
                "the session row open.",
                "Presentation.AgentHub.Services.ConnectionTracker._connections holds Presentation.AgentHub.DTOs.ActiveConversationInfo.UserId"),
        };

    /// <summary>
    /// Types that keep records about a person in memory that the holder rule cannot see, because no record names an
    /// owner field (a collection keyed by a run id), each with the reason it holds no personal data beyond its own use.
    /// Every entry carries a <see cref="ReviewedFieldByField"/> review, the only thing that would notice what it starts
    /// holding.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UnseenByTheHolderRule =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Infrastructure.AI/Infrastructure.AI.Runs.InMemoryRunProgressBroker"] =
                "Workflow progress for whoever is watching a run, keyed by run id (#1005). Each RunProgressEvent carries " +
                "the run id, a sequence number, the kind, the time, the step id and name, a status, and a detail: the " +
                "plan's name, the step's type, or how the run ended, as the run record's caller-safe error or a fixed " +
                "phrase (the cancellation notices RunCancellation's callers pass, and the executors' caller-safe failure " +
                "messages, are text reaching the pinned callers from further up, and are not pinned themselves). " +
                "PlanProgressRunBridge drops a step's output summary (PlanProgressRunBridgeTests); no event " +
                "carries step output, prompts or tool arguments. All of it repeats the run store and the plan store, which " +
                "erase and export it. Events are buffered only for an open watcher (publishing to none buffers nothing) " +
                "and leave with its subscription; the per-caller stream count, keyed tenant|owner, is removed when it " +
                "reaches zero. Erasure ends the person's live runs first (#992), and a run's finished or parked event " +
                "ends its streams (WorkflowProgressStreamer).",
        };

    /// <summary>
    /// For an exemption whose reason rests on what a holder's records carry: every field of the types the reason was
    /// checked against, and every production type that calls the holder's contract directly, which can put something new
    /// in a field whose shape did not change (#1005). What reaches a direct caller from further up is not pinned; see the
    /// field-by-field guard's remarks. Keyed by the holder's scan key.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Review> ReviewedFieldByField =
        new Dictionary<string, Review>(StringComparer.Ordinal)
        {
            ["Presentation.AgentHub/Presentation.AgentHub.Services.ConnectionTracker"] = new(
                [
                    "Presentation.AgentHub/Presentation.AgentHub.Services.ConnectionTracker",
                    "Presentation.AgentHub/Presentation.AgentHub.DTOs.ActiveConversationInfo",
                    "Presentation.AgentHub/Presentation.AgentHub.Interfaces.IConnectionTracker",
                ],
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.AgentName: System.String",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.ConversationId: System.String",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.LastActivityAt: System.DateTimeOffset",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.ObservabilitySessionId: System.Guid",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.StartedAt: System.DateTimeOffset",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.ToolCallCount: System.Int32",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.TotalCacheRead: System.Int32",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.TotalCacheWrite: System.Int32",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.TotalCostUsd: System.Decimal",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.TotalInputTokens: System.Int32",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.TotalOutputTokens: System.Int32",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.TurnCount: System.Int32",
                "Presentation.AgentHub.DTOs.ActiveConversationInfo.UserId: System.String",
                "Presentation.AgentHub.Interfaces.IConnectionTracker called by Presentation.AgentHub/Presentation.AgentHub.Services.ConversationOrchestrator",
                "Presentation.AgentHub.Interfaces.IConnectionTracker called by Presentation.AgentHub/Presentation.AgentHub.Services.SessionIdleCleanupService",
                "Presentation.AgentHub.Services.ConnectionTracker._connections: System.Collections.Concurrent.ConcurrentDictionary<System.String, Presentation.AgentHub.DTOs.ActiveConversationInfo>"),
            ["Infrastructure.AI/Infrastructure.AI.Runs.InMemoryRunProgressBroker"] = new(
                [
                    "Infrastructure.AI/Infrastructure.AI.Runs.InMemoryRunProgressBroker",
                    "Domain.AI/Domain.AI.Runs.RunProgressEvent",
                    "Application.AI.Common/Application.AI.Common.Interfaces.Runs.IRunProgressBroker",
                ],
                "Application.AI.Common.Interfaces.Runs.IRunProgressBroker called by Application.AI.Common/Application.AI.Common.Services.Runs.RunCancellation",
                "Application.AI.Common.Interfaces.Runs.IRunProgressBroker called by Infrastructure.AI/Infrastructure.AI.Runs.PlanProgressRunBridge",
                "Application.AI.Common.Interfaces.Runs.IRunProgressBroker called by Infrastructure.AI/Infrastructure.AI.Runs.RunDispatchBackgroundService",
                "Application.AI.Common.Interfaces.Runs.IRunProgressBroker called by Infrastructure.AI/Infrastructure.AI.Runs.RunProgressReclaimListener",
                "Application.AI.Common.Interfaces.Runs.IRunProgressBroker called by Presentation.ExecutionApi/Presentation.ExecutionApi.Controllers.WorkflowsController",
                "Domain.AI.Runs.RunProgressEvent.Detail: System.String",
                "Domain.AI.Runs.RunProgressEvent.JobId: System.String",
                "Domain.AI.Runs.RunProgressEvent.Kind: Domain.AI.Runs.RunProgressKind",
                "Domain.AI.Runs.RunProgressEvent.OccurredAt: System.DateTimeOffset",
                "Domain.AI.Runs.RunProgressEvent.Sequence: System.Int64",
                "Domain.AI.Runs.RunProgressEvent.Status: System.String",
                "Domain.AI.Runs.RunProgressEvent.StepId: System.String",
                "Domain.AI.Runs.RunProgressEvent.StepName: System.String",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription.Capacity: System.Int32",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription.Channel: System.Threading.Channels.Channel<Domain.AI.Runs.RunProgressEvent>",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription._broker: Infrastructure.AI.Runs.InMemoryRunProgressBroker",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription._disposed: System.Int32",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription._dropped: System.Int64",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription._jobId: System.String",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription._principal: System.String",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker._config: Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig>",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker._openSubscriptions: System.Int32",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker._pendingForget: System.Collections.Concurrent.ConcurrentDictionary<System.String, System.Byte>",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker._perPrincipal: System.Collections.Concurrent.ConcurrentDictionary<System.String, System.Int32>",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker._sequences: System.Collections.Concurrent.ConcurrentDictionary<System.String, System.Int64>",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker._time: System.TimeProvider",
                "Infrastructure.AI.Runs.InMemoryRunProgressBroker._watchers: System.Collections.Concurrent.ConcurrentDictionary<System.String, System.Collections.Concurrent.ConcurrentDictionary<Infrastructure.AI.Runs.InMemoryRunProgressBroker+Subscription, System.Byte>>"),
        };

    /// <summary>The types a field-by-field review read, and what the scan saw in them when it was written.</summary>
    /// <param name="types">
    /// Scan keys: a class or record contributes every field of it and of the types nested in it, an interface every
    /// production type that calls it.
    /// </param>
    /// <param name="evidence">The lines the scan produced for <paramref name="types"/> when the review was written.</param>
    internal sealed class Review(string[] types, params string[] evidence)
    {
        /// <summary>The scan keys of the types the review read.</summary>
        public IReadOnlyList<string> Types { get; } = types;

        /// <summary>What the scan saw in them.</summary>
        public IReadOnlyList<string> Evidence { get; } = evidence;
    }
}

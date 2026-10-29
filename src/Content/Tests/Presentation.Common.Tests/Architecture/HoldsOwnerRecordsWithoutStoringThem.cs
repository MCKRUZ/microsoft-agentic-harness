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
                "AgentHub presence: for each open SignalR connection, which conversation it is on and that session's " +
                "running totals. The entry goes when the connection closes (HandleDisconnectAsync) or after " +
                "AgentHub:SessionIdleTimeoutMinutes (5) without activity (SessionIdleCleanupService). Every field repeats " +
                "the conversation record or the observability session, which the conversation store and, when switched on, " +
                "the observability database hold, erase and export. Removing an entry outside those two paths would skip the active-connections gauge " +
                "and leave the session row open.",
                "Presentation.AgentHub.Services.ConnectionTracker._connections holds Presentation.AgentHub.DTOs.ActiveConversationInfo.UserId"),
        };
}

using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// High-level knowledge memory operations enabling agents to learn across conversations.
/// Provides Remember/Recall/Forget/Improve semantics inspired by Cognee's cognitive
/// architecture, with two-tier retrieval (session cache first, then graph).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RememberAsync"/> stores facts in the session-local cache for fast retrieval
/// within the current conversation. Facts are flushed to the permanent knowledge graph
/// via <see cref="ISessionKnowledgeCache.FlushToGraphAsync"/> at session end.
/// </para>
/// <para>
/// <see cref="RecallAsync"/> searches session cache first (sub-millisecond), falling back
/// to graph traversal when no local match is found. This two-source pattern minimizes
/// latency for recently-learned facts while maintaining full graph coverage.
/// </para>
/// <para>
/// <see cref="ImproveAsync"/> combines feedback detection with weight updates,
/// allowing the knowledge graph to learn from conversational quality signals.
/// </para>
/// </remarks>
public interface IKnowledgeMemory
{
    /// <summary>
    /// Stores a fact in the session-local cache, associated with the current
    /// knowledge scope (user, tenant, dataset). The fact is indexed for
    /// substring search and queued for graph persistence.
    /// </summary>
    /// <param name="key">A unique key for this memory entry.</param>
    /// <param name="content">The fact content to remember.</param>
    /// <param name="entityType">The entity type for graph node creation (e.g., "Fact", "Concept").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RememberAsync(
        string key,
        string content,
        string entityType = "Fact",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for relevant knowledge by query. Checks the session cache first
    /// for fast local hits, then falls back to graph neighborhood traversal.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching graph nodes ordered by relevance.</returns>
    Task<IReadOnlyList<GraphNode>> RecallAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a previously remembered fact from both the session cache and
    /// the permanent knowledge graph.
    /// </summary>
    /// <param name="key">The key of the memory entry to forget.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ForgetAsync(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies conversational feedback to improve knowledge graph quality.
    /// Detects implicit feedback from user messages and updates node/edge
    /// weights via the feedback store.
    /// </summary>
    /// <param name="userMessage">The user's message to analyze for feedback.</param>
    /// <param name="assistantResponse">The assistant response being reacted to.</param>
    /// <param name="relevantNodeIds">Node IDs that contributed to the assistant's response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ImproveAsync(
        string userMessage,
        string assistantResponse,
        IReadOnlyList<string> relevantNodeIds,
        CancellationToken cancellationToken = default);
}

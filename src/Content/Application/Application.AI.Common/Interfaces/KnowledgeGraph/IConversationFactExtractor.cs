// src/Content/Application/Application.AI.Common/Interfaces/KnowledgeGraph/IConversationFactExtractor.cs
using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Extracts notable facts from a user/assistant message pair using an LLM.
/// Facts are returned as structured <see cref="ConversationFact"/> records
/// for persistence via <see cref="IKnowledgeMemory.RememberAsync"/>.
/// </summary>
/// <remarks>
/// Implementations should:
/// <list type="bullet">
///   <item><description>Use an economy-tier model via <c>IModelRouter.RouteOperationAsync</c></description></item>
///   <item><description>Return an empty list for routine turns (greetings, acknowledgments)</description></item>
///   <item><description>Catch all exceptions internally and return an empty list on failure</description></item>
///   <item><description>Wrap user content in XML tags to defend against prompt injection</description></item>
/// </list>
/// </remarks>
public interface IConversationFactExtractor
{
    /// <summary>
    /// Analyzes a conversation turn and extracts notable facts.
    /// </summary>
    /// <param name="userMessage">The user's message from this turn.</param>
    /// <param name="assistantResponse">The assistant's response from this turn.</param>
    /// <param name="conversationId">Conversation ID for deterministic key generation.</param>
    /// <param name="turnNumber">Turn number for deterministic key generation.</param>
    /// <param name="cancellationToken">Cancellation token with extraction timeout.</param>
    /// <returns>
    /// Extracted facts in extraction order. Empty list when no notable
    /// facts are found or when extraction fails.
    /// </returns>
    Task<IReadOnlyList<ConversationFact>> ExtractAsync(
        string userMessage,
        string assistantResponse,
        string conversationId,
        int turnNumber,
        CancellationToken cancellationToken = default);
}

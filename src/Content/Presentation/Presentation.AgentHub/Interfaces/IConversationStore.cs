using Presentation.AgentHub.Models;

namespace Presentation.AgentHub.Interfaces;

/// <summary>
/// Persistent store for conversation records. Thread-safe for concurrent access.
/// Implementations must enforce user-ownership isolation — callers are responsible
/// for checking <see cref="ConversationRecord.UserId"/> against the authenticated user
/// before returning records to clients.
/// </summary>
public interface IConversationStore
{
    /// <summary>Returns the conversation with the given ID, or <c>null</c> if it does not exist.</summary>
    Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns all conversations owned by <paramref name="userId"/>.
    /// O(n) in the number of stored conversations — acceptable for POC scale.
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>Creates a new conversation with a generated GUID id.</summary>
    Task<ConversationRecord> CreateAsync(string agentName, string userId, CancellationToken ct = default);

    /// <summary>Appends <paramref name="message"/> to an existing conversation record.</summary>
    Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct = default);

    /// <summary>Permanently deletes a conversation record.</summary>
    Task DeleteAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the last <paramref name="maxMessages"/> messages from the conversation,
    /// or <c>null</c> if the conversation does not exist.
    /// Called by the hub before dispatching to the agent to prevent unbounded token growth.
    /// </summary>
    Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
        string conversationId,
        int maxMessages,
        CancellationToken ct = default);
}

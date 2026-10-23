namespace Domain.Common.Compliance;

/// <summary>What an erasure of one store removes for a subject: everything the person owns, or only what the named
/// conversations hold (#901).</summary>
/// <remarks>
/// Right-to-erasure uses every store whatever its reach. Conversation expiry removes one idle conversation and what it
/// produced, so it asks only the stores whose reach is <see cref="NamedConversations"/>; asking an
/// <see cref="Owner"/>-reach store would delete the rest of the person's data with it.
/// </remarks>
public enum ErasureReach
{
    /// <summary>
    /// Removes everything the store holds for the owner, whichever conversation it came from, such as their schedules,
    /// plans or ingested documents. The default, so a store that has not said otherwise is never used to expire one
    /// conversation.
    /// </summary>
    Owner,

    /// <summary>
    /// Removes only what belongs to <see cref="ErasureSubject.AllConversationIds"/> (and, for the tool-result and budget
    /// stores, the subject's planned-task run ids), and nothing when those are empty.
    /// </summary>
    NamedConversations,
}

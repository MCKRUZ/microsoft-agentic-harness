namespace Domain.Common.Compliance;

/// <summary>
/// The person a right-to-erasure request is for, in every form a store may have recorded them (#900).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two forms of the owner, because the writers disagree.</strong> Conversations and ingested document chunks
/// record the user id exactly as the identity provider issued it; plans, schedules and the knowledge graph record it
/// trimmed and lower-cased. An eraser must match the form its own writer stored, or it deletes nothing and still
/// reports success. Each eraser documents which one it reads.
/// </para>
/// <para>
/// <strong>Conversation ids are collected before anything is deleted.</strong> Several stores (observability sessions,
/// tool-result spills, budget rows, prompt-usage records, the call-once ledger) carry no owner, only the conversation
/// they belong to. They are reachable only through the subject's conversations, so the ids are read once, up front, and
/// the conversations themselves are erased last.
/// </para>
/// </remarks>
public sealed record ErasureSubject
{
    /// <summary>Initializes a new instance of the <see cref="ErasureSubject"/> class.</summary>
    /// <param name="ownerId">The user id as issued; never blank.</param>
    /// <param name="conversationIds">The subject's conversation ids, read before any deletion.</param>
    /// <exception cref="ArgumentException"><paramref name="ownerId"/> is blank: a blank subject is never a wildcard.</exception>
    public ErasureSubject(string ownerId, IReadOnlyList<string> conversationIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(conversationIds);

        OwnerId = ownerId;
        CanonicalOwnerId = ownerId.Trim().ToLowerInvariant();
        ConversationIds = conversationIds;
    }

    /// <summary>The user id exactly as the identity provider issued it.</summary>
    public string OwnerId { get; }

    /// <summary>The user id trimmed and lower-cased, the form plans and the knowledge graph store.</summary>
    public string CanonicalOwnerId { get; }

    /// <summary>The subject's conversation ids, read before any deletion.</summary>
    public IReadOnlyList<string> ConversationIds { get; }
}

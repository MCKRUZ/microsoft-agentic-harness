using Application.AI.Common.Models.Conversations;

namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity for one message in a conversation. One row per message is what makes appending
/// an <c>INSERT</c> instead of a rewrite of the whole transcript.
/// </summary>
public sealed class ConversationMessageEntity
{
    /// <summary>
    /// Auto-increment primary key that doubles as the append-order key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordering by <see cref="Timestamp"/> would not survive two messages appended in the same
    /// clock tick, and a per-conversation sequence number would have to be computed as
    /// <c>MAX(seq) + 1</c> — the read-modify-write this design exists to remove, and a lost-update
    /// under concurrency. SQLite assigns this key inside the insert, so ordinals are unique and
    /// monotonic no matter how many writers (or processes) append at once.
    /// </para>
    /// <para>
    /// Values are unique database-wide rather than per conversation. Nothing depends on them being
    /// contiguous — only on their relative order within one conversation.
    /// </para>
    /// </remarks>
    public long Ordinal { get; set; }

    /// <summary>Foreign key to the owning conversation.</summary>
    public required string ConversationId { get; set; }

    /// <summary>
    /// Stable message identity used by retry/edit flows. Distinct from <see cref="Ordinal"/>:
    /// this one is caller-visible and may be supplied by the client.
    /// </summary>
    public Guid MessageId { get; set; }

    /// <summary>
    /// Who sent the message. Persisted as the enum's name rather than its number, so inserting a
    /// value in the middle of <c>MessageRole</c> later cannot silently re-label existing rows.
    /// </summary>
    public MessageRole Role { get; set; }

    /// <summary>Message text. Empty for a message that carries only a widget.</summary>
    public required string Content { get; set; }

    /// <summary>When the message was created.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Serialized tool-call list, or null when the message made no tool calls.</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>Serialized generative-UI widget spec, or null for an ordinary text message.</summary>
    public string? WidgetJson { get; set; }

    /// <summary>Owning conversation.</summary>
    public ConversationEntity? Conversation { get; set; }
}

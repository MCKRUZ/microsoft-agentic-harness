namespace Application.AI.Common.Models.Conversations;

/// <summary>A single message in a conversation. Role determines rendering behavior in the UI.</summary>
/// <remarks>
/// The <see cref="Id"/> uniquely identifies the message within the conversation and is the
/// stable reference used by retry/edit operations. Clients MAY supply the id when appending
/// a user message (so optimistic UI and server record share the same id); otherwise the server
/// generates one. Legacy records with <see cref="Guid.Empty"/> ids are migrated on read.
/// <para>
/// <see cref="Widget"/> is set only on the assistant message that stands in for an inline generative-UI
/// render (image/form/table); such a message carries empty <see cref="Content"/> and the widget is
/// re-rendered from the spec on reload. It is null for every ordinary text message.
/// </para>
/// </remarks>
public sealed record ConversationMessage(
    Guid Id,
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCallRecord>? ToolCalls = null,
    WidgetSpec? Widget = null)
{
    /// <summary>
    /// This turn's tool calls, or <see langword="null"/> when it made none. Normalized to
    /// <see langword="null"/> for an empty (but non-null) list at construction, so every producer of a
    /// <see cref="ConversationMessage"/> gets the same guarantee for free instead of each one having to
    /// remember the ternary itself — an empty non-null list would otherwise serialize to a non-null
    /// <c>"[]"</c> JSON column, which <c>EfCoreConversationStore</c>'s dispatch-window filter reads as
    /// "this row has tool calls" (it tests the column for non-null, not for non-empty).
    /// </summary>
    public IReadOnlyList<ToolCallRecord>? ToolCalls { get; init; } = ToolCalls is { Count: > 0 } ? ToolCalls : null;
}

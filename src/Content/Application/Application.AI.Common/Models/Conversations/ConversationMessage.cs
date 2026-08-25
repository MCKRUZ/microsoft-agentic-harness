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
    /// Backing field for <see cref="ToolCalls"/>. The initializer normalizes the primary constructor's
    /// argument; the property's <c>init</c> accessor normalizes every other way a value can arrive.
    /// </summary>
    /// <remarks>
    /// Both are needed, and neither is redundant. A field/property <em>initializer</em> runs only in the
    /// primary constructor — a record's compiler-generated copy constructor copies backing fields
    /// directly and does not re-run it — so an initializer alone leaves <c>with { ToolCalls = [] }</c>
    /// holding a non-null empty list. An <c>init</c> accessor alone would not compile the positional
    /// parameter away (the compiler does not auto-assign a positional parameter to a member the type
    /// declares itself; that is what <c>CS8907</c> reports), so the initializer is what consumes it.
    /// </remarks>
    private readonly IReadOnlyList<ToolCallRecord>? _toolCalls = ToolCalls is { Count: > 0 } ? ToolCalls : null;

    /// <summary>
    /// This turn's tool calls, or <see langword="null"/> when it made none. An empty (but non-null)
    /// list normalizes to <see langword="null"/> however it arrives — the primary constructor, an
    /// object initializer, or a <c>with</c> expression — so every producer of a
    /// <see cref="ConversationMessage"/> gets the same guarantee for free instead of each one having to
    /// remember the ternary itself.
    /// </summary>
    /// <remarks>
    /// The guarantee is load-bearing rather than cosmetic, and two call sites cite it as their reason
    /// for not normalizing themselves (<c>ConversationEntityMapper.ToEntity</c> and the store's own
    /// dispatch-window filter). An empty non-null list serializes to a non-null <c>"[]"</c> JSON column,
    /// which <c>EfCoreConversationStore</c>'s filter would read as "this row is model-relevant" — that
    /// filter tests the column for non-null — admitting an empty-content widget row into the prompt
    /// window that <c>FileSystemConversationStore</c>'s DTO-level filter correctly drops. That store
    /// filter now also excludes <c>"[]"</c> explicitly, so the two are belt and braces: this closes the
    /// hole at the source, and the filter stays correct for any row persisted while it was open.
    /// </remarks>
    public IReadOnlyList<ToolCallRecord>? ToolCalls
    {
        get => _toolCalls;
        init => _toolCalls = value is { Count: > 0 } ? value : null;
    }
}

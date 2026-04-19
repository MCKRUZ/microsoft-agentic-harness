namespace Presentation.AgentHub.DTOs;

/// <summary>A single message in a conversation. Role determines rendering behavior in the UI.</summary>
/// <remarks>
/// The <see cref="Id"/> uniquely identifies the message within the conversation and is the
/// stable reference used by retry/edit operations. Clients MAY supply the id when appending
/// a user message (so optimistic UI and server record share the same id); otherwise the server
/// generates one. Legacy records with <see cref="Guid.Empty"/> ids are migrated on read.
/// </remarks>
public sealed record ConversationMessage(
    Guid Id,
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCallRecord>? ToolCalls = null);

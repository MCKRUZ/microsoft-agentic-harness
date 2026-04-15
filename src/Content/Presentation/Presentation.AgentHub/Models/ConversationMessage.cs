namespace Presentation.AgentHub.Models;

/// <summary>A single message in a conversation. Role determines rendering behavior in the UI.</summary>
public sealed record ConversationMessage(
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCallRecord>? ToolCalls = null);

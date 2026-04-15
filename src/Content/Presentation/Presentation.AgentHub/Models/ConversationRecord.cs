namespace Presentation.AgentHub.Models;

/// <summary>
/// Full conversation state persisted to disk. UserId is the object ID (OID claim)
/// of the owning Azure AD user. Never expose records to users other than the owner.
/// </summary>
public sealed record ConversationRecord(
    string Id,
    string AgentName,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationMessage> Messages);

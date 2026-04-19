namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Full conversation state persisted to disk. UserId is the object ID (OID claim)
/// of the owning Azure AD user. Never expose records to users other than the owner.
/// </summary>
/// <remarks>
/// <see cref="Title"/> is auto-derived from the first user message (truncated to
/// <c>ConversationRecordTitleDerivation.MaxLength</c> characters) when the first
/// message is appended. Absent on new records and on records predating this field.
/// </remarks>
public sealed record ConversationRecord(
    string Id,
    string AgentName,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationMessage> Messages,
    string? Title = null);

/// <summary>Title derivation rules — shared between store and any future rename logic.</summary>
public static class ConversationRecordTitleDerivation
{
    /// <summary>Maximum characters retained from the first user message when deriving a title.</summary>
    public const int MaxLength = 60;

    /// <summary>Collapses whitespace and truncates to <see cref="MaxLength"/>; appends an ellipsis when truncated.</summary>
    public static string Derive(string content)
    {
        var collapsed = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= MaxLength)
            return collapsed;
        return collapsed[..MaxLength].TrimEnd() + "\u2026";
    }
}

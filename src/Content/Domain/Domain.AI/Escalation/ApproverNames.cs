namespace Domain.AI.Escalation;

/// <summary>
/// The single source of truth for how approver names are compared against an
/// escalation's roster. Every roster lookup — the decide path, the pending-list
/// path, and the approval-strategy evaluation — must use this comparer so a
/// caller whose identity casing differs from the roster entry is treated
/// identically on all of them.
/// </summary>
/// <remarks>
/// Comparison is case-insensitive ordinal: approver names are operator-authored
/// identifiers (roster strings in gate config, token claims over HTTP), where
/// casing differences are accidental, not semantic. Normalizing at comparison
/// time — rather than rewriting names at roster construction — preserves the
/// original casing in audit records. A site that restates
/// <see cref="StringComparer.OrdinalIgnoreCase"/> inline instead of using this
/// member can silently drift back to case-sensitive matching; always reference
/// <see cref="Comparer"/>.
/// </remarks>
public static class ApproverNames
{
    /// <summary>The comparer used for every approver-name/roster comparison.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}

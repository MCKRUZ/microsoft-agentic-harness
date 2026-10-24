namespace Domain.Common.Compliance;

/// <summary>
/// The conversation ids one person owns, as an erasure request reads them before deleting anything (#907).
/// </summary>
/// <param name="Ids">The ids of every conversation the store could read and found owned by the person.</param>
/// <param name="Unreadable">
/// How many stored conversations could not be read, so whether they are the person's is unknown. Never skipped silently:
/// the erasure receipt reports them, because one of them may be the person's.
/// </param>
public sealed record ConversationIdListing(IReadOnlyList<string> Ids, int Unreadable)
{
    /// <summary>No conversations, and nothing unreadable.</summary>
    public static ConversationIdListing Empty { get; } = new([], 0);
}

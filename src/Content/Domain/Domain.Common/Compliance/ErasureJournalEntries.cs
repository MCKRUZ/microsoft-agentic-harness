namespace Domain.Common.Compliance;

/// <summary>
/// What an erasure request read for one person before deleting anything, kept so that a later request can still find
/// what the deleted conversations and plans led to (#907).
/// </summary>
/// <param name="ConversationIds">The person's conversation ids the request listed.</param>
/// <param name="WithheldConversationIds">
/// Their conversations whose ids have the reserved shape. Their records in the stores without an owner are never erased,
/// so these entries outlive a successful request and age out on their own, keeping later receipts honest.
/// </param>
/// <param name="PlanRuns">The ids the person's planned-task runs recorded data under.</param>
public sealed record ErasureJournalEntries(
    IReadOnlyList<string> ConversationIds,
    IReadOnlyList<string> WithheldConversationIds,
    PlanRunTraces PlanRuns)
{
    /// <summary>Nothing recorded.</summary>
    public static ErasureJournalEntries Empty { get; } = new([], [], PlanRunTraces.Empty);

    /// <summary>Whether nothing is recorded.</summary>
    public bool IsEmpty => ConversationIds.Count == 0 && WithheldConversationIds.Count == 0 && PlanRuns.IsEmpty;
}

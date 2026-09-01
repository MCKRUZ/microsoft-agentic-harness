namespace Domain.Common.Config.AI.ContextManagement;

/// <summary>
/// Governs the sweep that reclaims spilled tool-result files nothing will ever fetch again (#559).
/// Bound from <c>AppConfig:AI:ContextManagement:ToolResultRetention</c>.
/// </summary>
/// <remarks>
/// Modelled on <c>ConversationBudgetRetentionConfig</c>: same shape, same "why age is the right test
/// here" reasoning adapted to what this store actually is — see <c>IToolResultStore.PruneExpiredAsync</c>
/// for why age is an acceptable (if coarser) proxy for "the owning scope is gone" in this case, unlike
/// the conversation-budget sweep it is modelled on.
/// </remarks>
public sealed class ToolResultRetentionConfig
{
    /// <summary>Whether the sweep runs. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// On by default for the same reason <c>ConversationBudgetRetentionConfig.Enabled</c> is: the
    /// growth it reclaims is present on every deployment that spills any tool result at all, which
    /// every stock host does the moment one tool call exceeds <c>PerResultCharLimit</c>.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the sweep runs. Defaults to one hour.</summary>
    /// <remarks>
    /// Shorter than <c>ConversationBudgetRetentionConfig.SweepInterval</c>'s six hours: a spilled
    /// result can be considerably larger than a budget row (up to <c>MaxSpillChars</c>, megabytes
    /// rather than tens of bytes), so letting expired files accumulate for a full six hours costs
    /// real disk, not an unnoticeable amount of it.
    /// </remarks>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a spilled file must sit untouched, by last-write time, before it is reclaimed.
    /// Defaults to 24 hours.
    /// </summary>
    /// <remarks>
    /// Sized against how long a caller plausibly still wants to page through a truncated result, not
    /// against how long a conversation might stay idle — a spilled tool result is a byproduct of one
    /// turn, not a durable record a caller returns to weeks later the way a conversation is. Raise
    /// this if a deployment's agents routinely resume very old conversations and expect old spills to
    /// still be fetchable; nothing else here needs changing to support that.
    /// </remarks>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(24);
}

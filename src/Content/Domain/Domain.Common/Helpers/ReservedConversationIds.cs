namespace Domain.Common.Helpers;

/// <summary>
/// The conversation-id shape the harness keeps for ids it derives itself, so that no conversation a person opens can
/// take one (#907).
/// </summary>
/// <remarks>
/// <para>
/// A planned-task run records data under ids built from the run with <see cref="Separator"/> between the parts: each
/// model step runs as conversation <c>{runScope}:{stepId}</c>, and the run's budget is kept under
/// <c>planrun:{runScope}</c>. Several stores keyed by conversation id carry no owner (observability sessions, tool-result
/// spills, prompt-usage records, budgets, the call-once ledger), so erasing a conversation erases whatever those stores
/// hold under its id. If a person could open a conversation named like one of those keys, erasing that person would
/// erase another person's planned-task records with it.
/// </para>
/// <para>
/// So a conversation a caller names may not contain the separator: the conversation stores refuse to create one, and
/// the entry points that take a caller's id refuse it up front. A conversation created with such an id before this
/// rule existed still opens, but erasure does not follow its id into the stores without an owner, because it cannot
/// tell that conversation's records from a planned task's.
/// </para>
/// <para>
/// The rule closes shapes, not knowledge: a caller who learns another person's server-minted id (a job id, say) and
/// names a conversation after it is not stopped here. Those ids are random and are only shown to their owner. Since the
/// personal-data export (#902) reads those stores through the caller's conversation ids, such a caller's export would
/// return what the other person's run recorded there, so the consequence is disclosure, not only deletion. New tool-result
/// spills keep ids that differ only by case apart, but on Windows and macOS a spill folder written before #928 holds the
/// spills of every case variant that wrote to it, and the id that created the folder erases and exports all of them,
/// until retention removes the folder; see <c>FileSystemToolResultStore</c>.
/// </para>
/// </remarks>
public static class ReservedConversationIds
{
    /// <summary>The separator in every id the harness derives from a planned-task run.</summary>
    public const char Separator = ':';

    /// <summary>Whether <paramref name="conversationId"/> has the shape kept for ids the harness derives.</summary>
    /// <param name="conversationId">The id to check.</param>
    /// <returns><see langword="true"/> when the id contains <see cref="Separator"/>.</returns>
    public static bool IsReserved(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        return conversationId.Contains(Separator, StringComparison.Ordinal);
    }
}

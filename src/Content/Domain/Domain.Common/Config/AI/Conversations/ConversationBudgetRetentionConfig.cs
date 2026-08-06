namespace Domain.Common.Config.AI.Conversations;

/// <summary>
/// Governs the sweep that reclaims conversation-budget rows nothing can ever read again.
/// Bound from <c>AppConfig:AI:Conversations:BudgetRetention</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why age alone is not the test.</strong> A budget row records how much of a lifetime ceiling a
/// conversation has spent. Deleting one for a conversation someone later resumes silently resets that
/// ceiling — the exact failure the whole budget subsystem exists to prevent — and a conversation may
/// legitimately sit idle for months and come back. So the sweep requires a second condition: the row's
/// <em>conversation must no longer exist</em>. A deleted conversation cannot be resumed, so removing its
/// total cannot reset a ceiling anyone will meet again.
/// </para>
/// <para>
/// <strong>That protects durable conversations absolutely, and nothing else.</strong> Stated plainly
/// because the tempting shorter version — "no conversation row means nobody can resume it" — is false.
/// A budget key is caller-chosen and opaque, and several writers produce keys that never had a row in
/// <c>conversations</c> at all rather than having lost one:
/// </para>
/// <list type="bullet">
///   <item>plan runs, under a <c>planrun:</c>-prefixed scope;</item>
///   <item>self-contained conversation runs — the path taken when no conversation owner is supplied —
///     which accrue against a caller-supplied id with no transcript behind it;</item>
///   <item>any future caller, under whatever key it chooses.</item>
/// </list>
/// <para>
/// For those, <see cref="GracePeriod"/> is the only protection there is, which is why it is generous.
/// It answers a far easier question than user absence: how long before an operation this sweep cannot
/// recognise is certainly over. Plan runs are bounded by their own timeout, in minutes. A self-contained
/// run keyed by a stable, caller-supplied id is the case to size against — <strong>if a caller reuses
/// one such id after a gap longer than this period, its ceiling starts again</strong>. Raise the period
/// if that describes a deployment; nothing else here needs changing.
/// </para>
/// <para>
/// This bounds the table at roughly one row per conversation that still exists, plus recent unrecognised
/// keys — near enough the bound the conversation table itself carries. It deliberately does <em>not</em>
/// reproduce the in-process tracker's fixed 50,000-entry LRU cap: evicting a live conversation's total
/// to satisfy a row count is the silent-reset failure again, chosen deliberately rather than arrived at
/// by abandonment.
/// </para>
/// </remarks>
public sealed class ConversationBudgetRetentionConfig
{
    /// <summary>
    /// Whether the sweep runs. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// On by default because the growth it reclaims is now present on every deployment: the conversation
    /// ceiling itself ships enabled, so every stock host writes a row per conversation and, before this
    /// existed, never removed one. A host that switches this off keeps every orphaned row forever, which
    /// is the state this shipped in — supported, but a choice rather than a default.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the sweep runs. Defaults to six hours.
    /// </summary>
    /// <remarks>
    /// Reclaiming an orphaned row is never urgent — the row is inert, costs about sixty bytes, and
    /// nothing behaves differently while it lingers. The interval is therefore set to be
    /// unnoticeable rather than prompt.
    /// </remarks>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a row with no conversation must sit untouched before it is reclaimed. Defaults to
    /// thirty days.
    /// </summary>
    /// <remarks>
    /// This is the only protection for budget keys that never had a conversation row — see the
    /// type-level remarks for which writers produce those and how to size against them. It has no effect
    /// on durable conversations: those are never swept at all while their conversation exists, however
    /// long they have been idle, so shortening this does not make the sweep more aggressive about them.
    /// </remarks>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(30);
}

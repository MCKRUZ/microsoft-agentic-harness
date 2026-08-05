namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity holding one budget key's cumulative token spend, so a ceiling that spans turns also
/// spans the processes running them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately not a column on <see cref="ConversationEntity"/>, and deliberately unrelated to
/// it.</strong> The budget is keyed by an arbitrary caller-supplied string, and two of the four callers
/// have no conversation row at all: plan runs namespace their own keys
/// (<c>Domain.AI.Planner.PlanRunKeys.RunBudgetKey</c>), and <c>RunConversationCommandHandler</c> never
/// touches conversation storage. A column on the header would serve one caller and force the rest back
/// into a second mechanism. There is therefore no foreign key here, and adding one would break the
/// callers this table exists to serve.
/// </para>
/// <para>
/// Carries no concurrency token, for the same reason <see cref="ConversationEntity"/> carries none:
/// accrual is issued as a single atomic upsert that increments in the database, never as a
/// read-modify-write, so two concurrent turns sum rather than collide.
/// </para>
/// <para>
/// Rows are not deleted when a conversation ends — the two interactive callers have no "ended" signal
/// to act on. <see cref="UpdatedAt"/> records when a key last spent, which is what a retention sweep
/// would select on and what an operator reads to tell a live conversation from an abandoned one. No
/// such sweep exists yet, and no index serves one yet; at roughly 60 bytes per row, 50,000 abandoned
/// keys cost about 3 MB, so the growth is bounded enough to decide that separately.
/// </para>
/// </remarks>
public sealed class ConversationBudgetEntity
{
    /// <summary>
    /// The opaque budget key. A conversation id, a <c>planrun:</c>-prefixed run scope, or anything else
    /// a caller chooses; this table never interprets it.
    /// </summary>
    public required string BudgetKey { get; set; }

    /// <summary>
    /// Cumulative input+output tokens recorded against this key. Held as a 64-bit value so a long-lived
    /// conversation cannot overflow the running total; the status projection clamps it to
    /// <see cref="int.MaxValue"/> on the way out.
    /// </summary>
    public long ConsumedTokens { get; set; }

    /// <summary>When this key's total was last incremented. Drives age-based retention.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

namespace Domain.AI.Bundles;

/// <summary>
/// Outcome of offering a bundle run to the store: admitted, or refused by a named limit.
/// </summary>
/// <remarks>
/// <para>
/// Named rather than boolean because the two refusals mean opposite things to a caller. Being at
/// capacity is about the caller and clears on its own as its own work finishes; a conversation already
/// running is about that one conversation and clears only when that run ends. A caller told merely "no"
/// cannot tell whether to back off or to stop asking.
/// </para>
/// <para>
/// <strong>A separate type from <see cref="Domain.AI.Runs.RunAdmission"/>, not a reuse of it — the two
/// conflict axes are not interchangeable.</strong> That enum's <c>TargetAlreadyRunning</c> asserts a
/// second run would corrupt shared state ("a stored workflow's execution state is singular... a second
/// concurrent run would share one state machine with the first"), which is false for bundles: a second
/// run against a live conversation merely queues behind that conversation's turn lease, wasting a
/// dispatch slot rather than corrupting anything (see <see cref="ConversationAlreadyRunning"/>).
/// Attaching that doc comment, or that member name, to the bundle path would misdescribe the actual
/// risk. <c>StartEvalRunCommandHandler</c> reuses <c>RunAdmission</c> across run kinds precisely because
/// its target-conflict meaning transfers there unchanged; it does not transfer here.
/// </para>
/// </remarks>
public enum BundleRunAdmission
{
    /// <summary>The run was stored and may be dispatched.</summary>
    Accepted = 0,

    /// <summary>
    /// The run names a <see cref="BundleRunRecord.ConversationId"/> that already has a live run. A
    /// conversation's turn lease is held for the whole multi-turn run, not per turn, so a second
    /// concurrent run against the same conversation would not corrupt state the way two concurrent plan
    /// runs would — it would simply queue behind the lease while occupying a dispatch slot doing nothing.
    /// Refusing at admission is cheaper than discovering that by exhausting the parallel degree.
    /// </summary>
    ConversationAlreadyRunning = 1,

    /// <summary>
    /// The owner already holds as many live bundle runs — queued or executing, background or streaming —
    /// as the host permits.
    /// </summary>
    /// <remarks>
    /// Bounds how much work one caller may have <em>accepted</em>, which neither the per-request rate
    /// limit nor the streaming concurrency limiter expresses on its own: a caller within both can
    /// otherwise occupy every parallel dispatch slot by volume alone. It is not a statement about how
    /// much runs concurrently; that is the host's dispatch degree, and it is not a fairness mechanism
    /// either.
    /// </remarks>
    OwnerAtCapacity = 2
}

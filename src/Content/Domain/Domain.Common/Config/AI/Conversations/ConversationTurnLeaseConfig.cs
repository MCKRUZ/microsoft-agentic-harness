namespace Domain.Common.Config.AI.Conversations;

/// <summary>
/// Timings for the durable turn lease that stops two hosts running a turn on one conversation at the
/// same time. Bound from <c>AppConfig:AI:Conversations:TurnLease</c>. Read only by the
/// <see cref="ConversationStoreProvider.Sqlite"/> provider — the in-process lease has no expiry to
/// configure, because a lock held in one process is released by that process or lost with it.
/// </summary>
public sealed class ConversationTurnLeaseConfig
{
    /// <summary>
    /// How long a claimed lease stays valid without renewal. Defaults to 60 seconds.
    /// </summary>
    /// <remarks>
    /// This is the window in which a host that dies mid-turn keeps the conversation blocked, so a
    /// shorter value frees a crashed conversation sooner. It is also the margin the holder has to
    /// renew in: a live turn whose host stalls for longer than this — a long stop-the-world pause,
    /// a suspended container — has its lease taken by someone else, which cancels the turn rather
    /// than letting two run. Below a few seconds the second effect starts to dominate.
    /// </remarks>
    public int ExpirySeconds { get; set; } = 60;

    /// <summary>
    /// How long a caller waiting for the lease sleeps between attempts to claim it. Defaults to
    /// 250 milliseconds.
    /// </summary>
    /// <remarks>
    /// A database lease cannot hand over the way a semaphore does, so a queued turn discovers the
    /// lease is free by asking again. This is therefore the latency a queued turn pays on top of the
    /// turn ahead of it, traded against how often an idle waiter queries the database.
    ///
    /// It also means waiting is <strong>not</strong> first-come-first-served: whichever waiter next
    /// happens to ask wins, so two turns queued at the same moment may run in either order. The
    /// in-process lease this replaces was ordered. See <c>IConversationTurnLease</c>.
    /// </remarks>
    public int PollIntervalMilliseconds { get; set; } = 250;
}

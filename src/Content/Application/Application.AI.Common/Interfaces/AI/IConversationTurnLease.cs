namespace Application.AI.Common.Interfaces.AI;

/// <summary>
/// Serialises turns on one conversation, so that two turns can never run against the same transcript
/// at the same time — whether they arrive at one host or at two.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists as a port rather than a lock.</strong> Concurrent turns on one conversation
/// were previously prevented by a per-conversation <c>SemaphoreSlim</c> held in the interactive host's
/// memory. That is sufficient while exactly one process runs turns, and it stopped being sufficient
/// the moment the transcript became durable and shareable: the AgentHub and the Execution API are
/// separate processes, and a semaphore in one of them says nothing to the other. Two turns
/// interleaving on one conversation produce a transcript in which the second turn's model call never
/// saw the first turn's messages, and whose message order reflects completion rather than causality.
/// </para>
/// <para>
/// <strong>Acquisition blocks; it does not fail.</strong> A caller that cannot take the lease waits
/// for it, exactly as waiting on the semaphore did — a second turn arriving mid-turn is queued, not
/// rejected. There is deliberately no <c>TryAcquireAsync</c>: no caller wants one today, and adding
/// the shape before there is a caller would invite a rejection path nobody has decided the behaviour
/// for. There is likewise no timeout, because there was none before; a caller that wants to stop
/// waiting cancels the token it passed in.
/// </para>
/// <para>
/// <strong>Waiting is not ordered, and callers must not assume it is.</strong> Nothing here promises
/// that queued turns are admitted in the order they arrived. The in-process implementation inherits
/// <see cref="SemaphoreSlim"/>'s first-in-first-out queue and does; the durable one cannot, because a
/// waiter on another host discovers the lease is free by asking again, so two turns queued at once
/// may run in either order. This is stated rather than fixed: making it ordered means handing out
/// tickets and admitting them in sequence, which then needs its own answer for a host that takes a
/// ticket and dies. Callers that need a strict order must impose it themselves — the transcript is
/// still consistent either way, because each turn sees everything the turn before it wrote.
/// </para>
/// <para>
/// <strong>Replace this and <see cref="IConversationStore"/> together.</strong> They are two halves
/// of one decision, and the harness registers them from one switch for that reason. A lease reaches
/// exactly as far as the store it was built for: the durable lease claims a conversation by writing
/// to the same row the durable store owns, so pairing it with a store that keeps transcripts
/// somewhere else leaves it looking for conversations that, as far as it can see, do not exist — and
/// every turn is refused. The reverse pairing is quieter and worse: an in-process lease under a
/// shared store serialises each host against itself and nothing against the other.
/// </para>
/// <para>
/// <strong>The lease takes no caller identity, and that is deliberate.</strong> Authorization happens
/// before leasing: every call site reads the conversation through
/// <see cref="IConversationStore.GetAsync"/> first, which refuses a conversation belonging to another
/// user. Adding a <c>callerId</c> parameter here would be a control only the durable implementation
/// could honour — the in-process one has no stored owner to compare against — and a control that one
/// implementation silently ignores is worse than no control, because it reads as enforcement.
/// </para>
/// </remarks>
public interface IConversationTurnLease
{
    /// <summary>
    /// Waits until this caller holds the turn lease for <paramref name="conversationId"/>, then
    /// returns the handle that holds it. Dispose the handle to release.
    /// </summary>
    /// <param name="conversationId">The conversation whose turn is being claimed.</param>
    /// <param name="ct">
    /// Cancels the wait. Once the lease is held, this token no longer affects it — the handle is
    /// released by disposing it, not by cancelling.
    /// </param>
    /// <returns>The held lease. Never null.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is blank.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting.</exception>
    /// <exception cref="InvalidOperationException">
    /// The conversation does not exist. Only a durable implementation can tell — an in-process one
    /// has no record to consult and leases any id it is given.
    /// </exception>
    Task<IConversationTurnLeaseHandle> AcquireAsync(string conversationId, CancellationToken ct = default);
}

/// <summary>
/// A held turn lease. Releasing it is the caller's responsibility: dispose it, always in a
/// <c>finally</c> or via <c>await using</c>.
/// </summary>
public interface IConversationTurnLeaseHandle : IAsyncDisposable
{
    /// <summary>
    /// Cancelled if this lease stops being held before it is disposed. <strong>Link it into the
    /// token driving the turn</strong> — a lease that has been lost is one another host now holds, so
    /// continuing to write to the transcript reintroduces exactly the concurrent turn the lease
    /// exists to prevent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A durable lease carries an expiry so that a host which crashes mid-turn does not block the
    /// conversation forever, and is renewed while the turn runs. A turn's wall-clock duration is
    /// unbounded — one slow tool call is enough — so renewal can fall behind (a stalled host, a
    /// paused process, a clock jump) and the lease can be taken by someone else while this caller
    /// still believes it holds it. This token is how that becomes visible rather than silent.
    /// </para>
    /// <para>
    /// An in-process implementation never loses a lease it holds, so its token is
    /// <see cref="System.Threading.CancellationToken.None"/> and linking it costs nothing.
    /// </para>
    /// </remarks>
    CancellationToken LeaseLost { get; }
}

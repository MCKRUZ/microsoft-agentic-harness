using Application.AI.Common.Interfaces.AI;

namespace Application.AI.Common.Services;

/// <summary>
/// One held <see cref="IConversationTurnLeaseHandle"/> together with the token a turn must run
/// under while it holds it, and the test for telling a lost lease apart from a client disconnect.
/// </summary>
/// <remarks>
/// <para>
/// Every path that produces a turn on a conversation — the SignalR orchestrator, the AG-UI run
/// handler, the durable run handler — does the same three things: wait for the lease, link the
/// handle's <see cref="IConversationTurnLeaseHandle.LeaseLost"/> into the token driving the turn,
/// and, when that token fires, decide whether the cause was the lease or the caller. This type is
/// the single place that shape lives, so a correction to it reaches every path.
/// </para>
/// <para>
/// What it deliberately does <em>not</em> own is what to do about a lost lease. The SignalR path
/// throws, the AG-UI path writes an error event into a stream that may still be open, and the
/// durable path lets the cancellation surface as a cancellation. Those are transport decisions;
/// <see cref="LeaseWasLost"/> is the shared question they each ask.
/// </para>
/// <para>
/// Disposal releases the linked token source first and the lease second, so nothing can observe a
/// token that outlives the lease it was linked to.
/// </para>
/// </remarks>
public sealed class LeasedTurn : IAsyncDisposable
{
    private readonly IConversationTurnLeaseHandle _lease;
    private readonly CancellationTokenSource _turnTokenSource;
    private readonly CancellationToken _leaseLost;
    private readonly CancellationToken _callerToken;

    private LeasedTurn(
        IConversationTurnLeaseHandle lease,
        CancellationTokenSource turnTokenSource,
        CancellationToken leaseLost,
        CancellationToken callerToken)
    {
        _lease = lease;
        _turnTokenSource = turnTokenSource;
        _leaseLost = leaseLost;
        _callerToken = callerToken;
    }

    /// <summary>
    /// The token the turn must run under: cancelled when the caller cancels <em>or</em> when the
    /// lease is lost. Linking it is the entire reason this type exists — a lease lost mid-turn is
    /// one another host now holds, so writing anything further is the concurrent turn the lease
    /// was taken to prevent.
    /// </summary>
    public CancellationToken Token => _turnTokenSource.Token;

    /// <summary>
    /// True when a cancellation seen under <see cref="Token"/> was caused by losing the lease
    /// rather than by the caller giving up.
    /// </summary>
    /// <remarks>
    /// Both halves of the test matter. When the caller has <em>also</em> cancelled, the disconnect
    /// is the honest explanation — and there is usually no longer a stream for the other
    /// explanation to reach — so a real disconnect that happens to race the loss is reported as a
    /// disconnect, not as a lost lease. The handle's
    /// <see cref="IConversationTurnLeaseHandle.LeaseLost"/> getter throws once a durable handle is
    /// disposed, so the token is read once at acquisition; this property is then safe to read at
    /// any point, including after the scope has been disposed.
    /// </remarks>
    public bool LeaseWasLost =>
        _leaseLost.IsCancellationRequested && !_callerToken.IsCancellationRequested;

    /// <summary>
    /// Waits until this caller holds the turn lease for <paramref name="conversationId"/>, then
    /// returns the scope that holds it. Dispose the scope to release.
    /// </summary>
    /// <param name="turnLease">The lease implementation to acquire from.</param>
    /// <param name="conversationId">The conversation whose turn is being claimed.</param>
    /// <param name="ct">
    /// The caller's token. Cancels the wait for the lease; once held, it is linked into
    /// <see cref="Token"/> alongside the lease's own lost signal.
    /// </param>
    /// <returns>The held lease and its linked token.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting.</exception>
    /// <exception cref="InvalidOperationException">The conversation does not exist (durable leases only).</exception>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is blank.</exception>
    public static async Task<LeasedTurn> AcquireAsync(
        IConversationTurnLease turnLease, string conversationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turnLease);

        var lease = await turnLease.AcquireAsync(conversationId, ct);

        try
        {
            var leaseLost = lease.LeaseLost;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, leaseLost);
            return new LeasedTurn(lease, linked, leaseLost, ct);
        }
        catch
        {
            // The caller has no scope to dispose yet, and a durable lease keeps renewing until it is
            // released — leaking it would block every later turn on the conversation until it expired.
            await lease.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            _turnTokenSource.Dispose();
        }
        finally
        {
            await _lease.DisposeAsync();
        }
    }
}

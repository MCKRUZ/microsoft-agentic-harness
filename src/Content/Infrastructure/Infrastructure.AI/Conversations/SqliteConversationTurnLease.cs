using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config.AI.Conversations;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Durable turn lease held as two columns on the conversation row, so that hosts which share nothing
/// in memory still take turns on a conversation one at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The claim is one conditional statement.</strong> Claiming runs a single <c>UPDATE</c> that
/// stamps this caller's token onto the row only where the lease is free or expired. Two hosts racing
/// to start a turn therefore both issue the same statement and SQLite serialises them: one updates a
/// row, the other updates none. Reading the lease first and writing second would reintroduce the
/// window the lease exists to close.
/// </para>
/// <para>
/// <strong>A lease expires, and is renewed while the turn runs.</strong> Without an expiry a host
/// that crashed mid-turn would block its conversation forever, with nothing to clear the row. With
/// only an expiry, a turn that outlives it — one slow tool call is enough, and turn duration has no
/// bound — would have its lease taken while it was still writing. So the holder renews on a timer at
/// a third of the expiry, and a renewal that matches no row means the lease is gone and the turn must
/// stop; that is what <see cref="IConversationTurnLeaseHandle.LeaseLost"/> reports.
/// </para>
/// <para>
/// <strong>Scope.</strong> This reaches as far as the database file does: several processes on one
/// machine, or several sharing one path. It is not a distributed lock — hosts on different machines
/// with different database files are not serialised by it, and neither are their transcripts, so that
/// deployment needs a server-backed <see cref="IConversationStore"/> and a lease to match. Expiry is
/// judged against each host's own clock, which is the same clock when they share a machine.
/// </para>
/// </remarks>
public sealed class SqliteConversationTurnLease : IConversationTurnLease
{
    private readonly IDbContextFactory<ConversationDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteConversationTurnLease> _logger;
    private readonly TimeSpan _expiry;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _renewInterval;

    /// <summary>Initializes the lease.</summary>
    /// <param name="contextFactory">Factory for short-lived contexts, one per statement.</param>
    /// <param name="config">Supplies the expiry and poll timings.</param>
    /// <param name="timeProvider">Clock for expiry stamps, renewal timing, and the wait between claims.</param>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="schemaInitializer">
    /// Demanded so that resolving this lease forces the schema to exist before its first statement
    /// runs, the same wiring <see cref="EfCoreConversationStore"/> uses. Nothing is done with the
    /// instance — constructing it is the whole effect. It matters here independently of the store,
    /// because a host is free to resolve the lease first.
    /// </param>
    /// <exception cref="ArgumentException">A configured timing is not positive.</exception>
    public SqliteConversationTurnLease(
        IDbContextFactory<ConversationDbContext> contextFactory,
        IOptions<ConversationsConfig> config,
        TimeProvider timeProvider,
        ILogger<SqliteConversationTurnLease> logger,
        SchemaInitializer<ConversationDbContext> schemaInitializer)
    {
        ArgumentNullException.ThrowIfNull(schemaInitializer);

        // Read once rather than through IOptionsMonitor: these timings are agreed between hosts by
        // configuration, and changing the expiry under a lease that is already held would move the
        // instant at which someone else may take it away from the holder that is renewing against
        // the old one.
        var lease = config.Value.TurnLease;
        Validate(lease, nameof(config));

        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _expiry = TimeSpan.FromSeconds(lease.ExpirySeconds);
        _pollInterval = TimeSpan.FromMilliseconds(lease.PollIntervalMilliseconds);

        // A third of the expiry, so two consecutive renewals can fail — a momentarily locked
        // database, a scheduling delay — and the third still lands before anyone else may claim.
        _renewInterval = _expiry / 3;
    }

    /// <summary>
    /// Rejects lease timings that cannot work. Called from the constructor and, so the failure lands
    /// at host startup rather than at whichever turn first resolves this singleton, from the
    /// registration too.
    /// </summary>
    /// <param name="lease">The configured timings.</param>
    /// <param name="parameterName">The argument to blame, so both callers report their own.</param>
    /// <exception cref="ArgumentException">A timing is not positive.</exception>
    /// <remarks>
    /// Neither value has a safe reading at zero: a lease that expires the instant it is taken
    /// serialises nothing at all, and a waiter that sleeps for no time between claims spins on the
    /// database. Both are misconfigurations, and a host that refuses to start reports one far better
    /// than a lease that silently stops doing its job.
    /// </remarks>
    public static void Validate(ConversationTurnLeaseConfig lease, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (lease.ExpirySeconds <= 0)
        {
            throw new ArgumentException(
                "AppConfig:AI:Conversations:TurnLease:ExpirySeconds must be greater than zero.",
                parameterName);
        }

        if (lease.PollIntervalMilliseconds <= 0)
        {
            throw new ArgumentException(
                "AppConfig:AI:Conversations:TurnLease:PollIntervalMilliseconds must be greater than zero.",
                parameterName);
        }
    }

    /// <inheritdoc />
    public async Task<IConversationTurnLeaseHandle> AcquireAsync(
        string conversationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        // Unique per acquisition, not per host: a host that lost a lease and then re-took it holds a
        // different token, so the renewal belonging to the lost one cannot resurrect it.
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

        // One context for the whole wait rather than one per attempt. The statements below track
        // nothing and hold no transaction between them, so a waiter behind a slow turn reuses this
        // instead of constructing a fresh context every poll.
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Look before writing. Claiming is an UPDATE, and SQLite takes a write lock and journals
            // for one even when it matches no rows — so a waiter that claimed on every poll would
            // contend with the holder's own appends and renewals for the whole time it waited, purely
            // to be told "no" again. A read costs neither. The lease is claimed only once it looks
            // claimable, and losing the race between the two is handled the only way it can be: the
            // conditional UPDATE still matches nothing, and the loop goes round.
            var state = await ReadLeaseAsync(context, conversationId, ct);

            // No row at all. Polling forever for a conversation that does not exist would hang the
            // caller with nothing said about why, so this is the one case worth distinguishing.
            if (state is null)
            {
                throw new InvalidOperationException(
                    $"Conversation '{conversationId}' does not exist, so no turn can be leased on it.");
            }

            if (IsClaimable(state.Value) && await TryClaimAsync(context, conversationId, owner, ct))
                return new Handle(this, conversationId, owner);

            await Task.Delay(_pollInterval, _timeProvider, ct);
        }
    }

    // -------------------------------------------------------------------------
    // Statements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the conversation's current lease state, or <c>null</c> when there is no such
    /// conversation.
    /// </summary>
    private static async Task<(string? Owner, DateTimeOffset? ExpiresAt)?> ReadLeaseAsync(
        ConversationDbContext context, string conversationId, CancellationToken ct)
    {
        var rows = await context.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new ValueTuple<string?, DateTimeOffset?>(c.LeaseOwner, c.LeaseExpiresAt))
            .ToListAsync(ct);

        // A list rather than FirstOrDefault: the projection is a value tuple, whose default is
        // indistinguishable from a genuine row holding no lease — which is exactly the free lease this
        // method's caller is waiting for, and would be read as "no such conversation".
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Whether a lease in this state may be taken: never held, or held past its expiry.</summary>
    private bool IsClaimable((string? Owner, DateTimeOffset? ExpiresAt) state) =>
        state.Owner is null
        || state.ExpiresAt is null
        || state.ExpiresAt.Value <= _timeProvider.GetUtcNow();

    /// <summary>
    /// Stamps <paramref name="owner"/> onto the conversation if the lease is still free or expired.
    /// Returns whether this caller now holds it.
    /// </summary>
    /// <remarks>
    /// The claimable test is repeated inside the <c>WHERE</c> clause even though the caller has
    /// already read it, and that repetition is the whole safety of this design: two hosts can both
    /// read a free lease, and only the statement that carries the condition can be the one that
    /// decides. Reading first is an optimisation; this is the check.
    /// </remarks>
    private async Task<bool> TryClaimAsync(
        ConversationDbContext context, string conversationId, string owner, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? expiresAt = now + _expiry;

        var claimed = await context.Conversations
            .Where(c => c.Id == conversationId
                && (c.LeaseOwner == null || c.LeaseExpiresAt == null || c.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.LeaseOwner, owner)
                    .SetProperty(c => c.LeaseExpiresAt, expiresAt),
                ct);

        return claimed > 0;
    }

    /// <summary>Pushes the expiry out. Returns false when this caller no longer holds the lease.</summary>
    private async Task<bool> TryRenewAsync(string conversationId, string owner, CancellationToken ct)
    {
        DateTimeOffset? expiresAt = _timeProvider.GetUtcNow() + _expiry;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var renewed = await context.Conversations
            .Where(c => c.Id == conversationId && c.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LeaseExpiresAt, expiresAt), ct);

        return renewed > 0;
    }

    /// <summary>Clears the lease, but only if this caller still holds it.</summary>
    /// <remarks>
    /// The owner match is what stops a released lease from clearing someone else's: a holder whose
    /// lease expired and was taken over would otherwise, on finishing, blank a lease the new holder
    /// is actively using.
    /// </remarks>
    private async Task ReleaseAsync(string conversationId, string owner, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        await context.Conversations
            .Where(c => c.Id == conversationId && c.LeaseOwner == owner)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.LeaseOwner, (string?)null)
                    .SetProperty(c => c.LeaseExpiresAt, (DateTimeOffset?)null),
                ct);
    }

    /// <summary>
    /// The held lease: renews itself on a timer, reports loss, and clears the row when disposed.
    /// </summary>
    private sealed class Handle : IConversationTurnLeaseHandle
    {
        private readonly SqliteConversationTurnLease _lease;
        private readonly string _conversationId;
        private readonly string _owner;
        private readonly CancellationTokenSource _lost = new();
        private readonly ITimer _renewTimer;

        private int _renewing;
        private int _disposed;

        /// <summary>
        /// The renewal currently running, so disposal can wait for it. Assigned only from
        /// <see cref="OnRenewTick"/> inside its own guard, so there is never more than one.
        /// </summary>
        private Task _renewal = Task.CompletedTask;

        public Handle(SqliteConversationTurnLease lease, string conversationId, string owner)
        {
            _lease = lease;
            _conversationId = conversationId;
            _owner = owner;
            _renewTimer = lease._timeProvider.CreateTimer(
                OnRenewTick, state: null, dueTime: lease._renewInterval, period: lease._renewInterval);
        }

        /// <inheritdoc />
        public CancellationToken LeaseLost => _lost.Token;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // Order matters, and stopping the timer is only half of it. Disposing the timer prevents
            // further callbacks, but a renewal already in flight keeps running — the callback starts
            // the work and returns rather than waiting for it. Releasing while that renewal is
            // mid-statement makes it match no row, and "matched no row" is how a lost lease is
            // detected: a turn that ended normally would log that it had been taken and cancel a
            // token nothing is listening to any more. So drain the renewal first; it then completes
            // against a row this handle still owns.
            await _renewTimer.DisposeAsync();
            await Volatile.Read(ref _renewal);

            try
            {
                // Deliberately not the turn's token: the turn is over, and a cancelled turn is
                // exactly when leaving the lease held until it expires is least acceptable.
                await _lease.ReleaseAsync(_conversationId, _owner, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Nothing is broken by failing to release — the lease expires on its own — so this
                // must not throw out of a `finally` at the call site and mask the turn's own outcome.
                _lease._logger.LogWarning(
                    ex,
                    "Failed to release the turn lease on conversation {ConversationId}; it will expire instead.",
                    _conversationId);
            }

            _lost.Dispose();
        }

        /// <summary>
        /// Timer callback. Returns immediately and lets the renewal run on its own, because the
        /// callback runs on a timer thread where a thrown exception takes the process down and a
        /// blocking wait stalls every other timer.
        /// </summary>
        private void OnRenewTick(object? _)
        {
            // A renewal still in flight when the next tick arrives means the database is slow; piling
            // a second one on top would make that worse rather than better.
            if (Interlocked.CompareExchange(ref _renewing, 1, 0) != 0)
                return;

            // Published so disposal can wait for it. Written before the guard is cleared, and read by
            // disposal only after the timer is dead, so what disposal awaits is either this renewal
            // or a completed one — never a tick that has yet to start.
            Volatile.Write(ref _renewal, RenewAsync());
        }

        private async Task RenewAsync()
        {
            try
            {
                // Not redundant with the drain in DisposeAsync, though it looks it. The tick calls
                // RenewAsync and only then publishes the returned task, so a disposal landing in that
                // gap reads the *previous*, completed task, drains nothing, and releases the lease
                // while this renewal is starting. Without this check that renewal would then match no
                // row and report a lost lease for a turn that ended normally.
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                if (await _lease.TryRenewAsync(_conversationId, _owner, CancellationToken.None))
                    return;

                // Matched no row: this lease expired and someone else has it, or the conversation is
                // gone. Either way this turn is no longer the one allowed to write.
                _lease._logger.LogWarning(
                    "Turn lease on conversation {ConversationId} was lost before the turn finished; cancelling it.",
                    _conversationId);

                _lost.Cancel();
            }
            catch (Exception ex)
            {
                // A failed renewal is not a lost lease: the row still names this owner until the
                // expiry passes, and the next tick has time to try again. Reporting a loss here would
                // cancel live turns over a momentarily locked database.
                _lease._logger.LogWarning(
                    ex,
                    "Failed to renew the turn lease on conversation {ConversationId}; retrying on the next tick.",
                    _conversationId);
            }
            finally
            {
                Volatile.Write(ref _renewing, 0);
            }
        }
    }
}

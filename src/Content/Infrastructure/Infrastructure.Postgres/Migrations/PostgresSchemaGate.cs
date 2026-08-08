using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// Runs a store's migrations at most once per process, on the first connection that needs them.
/// </summary>
/// <remarks>
/// <para>
/// Migrations run lazily on first use rather than at dependency-injection composition or from a
/// hosted service. The harness has several hosts — console, agent hub, execution API, MCP server —
/// and only some of them run hosted services; binding schema readiness to first use is the one point
/// that is guaranteed to happen in all of them, and it happens before the first query rather than
/// racing it.
/// </para>
/// <para>
/// A failed attempt does not latch. Postgres being briefly unreachable while the host starts is
/// ordinary, and a store that gave up permanently on the first refused connection would need a
/// restart to recover from a condition that fixes itself.
/// </para>
/// <para>
/// It does, however, cool down for <see cref="FailureCooldown"/>. Not latching is right for a
/// transient fault and wrong for a permanent one, and the runner now has a permanent one it can
/// report: a migration whose checksum no longer matches what this database applied will fail every
/// time until a human intervenes. Without a cooldown that failure is re-attempted on every physical
/// connection for the life of the process, each attempt taking the cluster-wide advisory lock to be
/// told the same thing. The cooldown keeps the recovery property — the next request after the window
/// tries again — while making a hopeless failure cheap.
/// </para>
/// <para>
/// <strong>Where to call it from.</strong> There are two shapes in this repo and the choice is not
/// arbitrary, so a third Postgres-backed store should copy the one that matches its own structure
/// rather than whichever it read first.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <em>A store with one place it opens connections</em> — <c>PostgreSqlGraphStore</c> — calls
/// <see cref="EnsureAsync"/> there. Simplest, and the preferred shape when the seam exists.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>A store with no such seam</em> — <c>PostgresObservabilityStore</c>, where eighteen call sites
/// take commands straight off an <c>NpgsqlDataSource</c> — hooks
/// <c>NpgsqlDataSourceBuilder.UsePhysicalConnectionInitializer</c> instead. That reaches every one of
/// them without threading a call through all eighteen, and it runs before the first query rather
/// than racing it. Note that it needs both the sync and async initializer; the sync one should throw,
/// because a synchronous <c>Open()</c> would skip the migration entirely.
/// </description>
/// </item>
/// </list>
/// <para>
/// A factory to unify the two was considered and is the wrong shape: what differs is not how the
/// data source is built but whether the store has a single point at which a connection becomes
/// available, which is a property of the store, not of its configuration.
/// </para>
/// <para>
/// Deliberately NOT <see cref="IDisposable"/>. It was, briefly, because it holds a
/// <see cref="SemaphoreSlim"/> — and the ceremony was immediately skipped by one of its two
/// consumers, which is the tell that it was never needed. A <see cref="SemaphoreSlim"/> only needs
/// disposing if its <c>AvailableWaitHandle</c> has been touched, which allocates the underlying
/// handle; nothing here touches it. Exporting a lifetime requirement that does not exist buys
/// nothing and gives every consumer a decision it can get wrong.
/// </para>
/// <para>
/// A public <c>Dispose</c> survived that removal for a while, on a type that no longer implements
/// the interface — so nothing called it, and nothing could have noticed. Do not put it back. The
/// observability store invokes this gate from its physical-connection initializer, so a consumer who
/// found the method and called it would leave every subsequent connection unable to open, and the
/// store would stop writing entirely.
/// </para>
/// </remarks>
public sealed class PostgresSchemaGate
{
    /// <summary>
    /// How long a failed attempt is reused before the database is asked again.
    /// </summary>
    /// <remarks>
    /// Short enough that a database which was briefly unreachable is picked up on the next request
    /// rather than needing a restart, long enough that a failure which will not fix itself — a
    /// migration whose checksum no longer matches, a missing privilege — is not re-attempted once per
    /// physical connection for the life of the process. Not configurable: nothing about a consumer's
    /// deployment makes a different number right, and an option here would be one more thing to get
    /// wrong for no gain.
    /// </remarks>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);

    private readonly PostgresMigrationRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private volatile bool _ready;

    // Both written and read only while holding _gate, so neither needs to be volatile.
    private ExceptionDispatchInfo? _lastFailure;
    private long _failedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresSchemaGate"/> class.
    /// </summary>
    /// <param name="options">Ledger table and advisory lock key for this migration set.</param>
    /// <param name="scripts">The migration set, in any order.</param>
    /// <param name="logger">Logger recording which migrations were applied.</param>
    /// <param name="timeProvider">
    /// Clock used for the failure cooldown; defaults to the system clock. Injectable so the cooldown
    /// can be tested without a test that sleeps.
    /// </param>
    /// <remarks>
    /// The gate builds its own runner rather than taking one, because both consumers were otherwise
    /// writing the same nested double-construction. There was briefly a second, runner-taking
    /// constructor kept "for the test fixture, which needs to hold the runner itself" — no such
    /// consumer ever existed, and the two fixtures that might have been it construct a
    /// <see cref="PostgresMigrationRunner"/> directly and never touch this class. A documented seam
    /// with no caller is worse than no seam: it survives refactors on the strength of a consumer
    /// nobody can find.
    /// </remarks>
    public PostgresSchemaGate(
        PostgresMigrationOptions options,
        IReadOnlyList<MigrationScript> scripts,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _runner = new PostgresMigrationRunner(options, scripts, logger);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Ensures the schema is current, running the migrations if this is the first caller to ask.
    /// </summary>
    /// <param name="connection">An open connection to the target database.</param>
    /// <param name="cancellationToken">Token to cancel the run.</param>
    /// <exception cref="PostgresMigrationException">A migration failed; the schema is unchanged.</exception>
    public async Task EnsureAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        if (_ready) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready) return;

            // Within the cooldown, hand back the failure that is already known rather than asking the
            // database again. The caller sees exactly what it would have seen; what it does not do is
            // make another round trip and take the cluster-wide advisory lock to be told the same
            // thing. The rethrown stack trace is the ORIGINAL attempt's, which is the useful one.
            //
            // Accepted, with eyes open: rethrowing restores dispatch state onto the one shared
            // exception object, so a caller that was served a moment ago and is now formatting it for
            // a log can see an interleaved stack trace. The alternative — wrapping a fresh exception
            // per replay — was rejected because it changes the TYPE the caller sees between the first
            // failure and the replays whenever the original was not a PostgresMigrationException, and
            // a caller catching by type getting different answers for the same fault is a worse
            // problem than an occasionally untidy log line during a five-second window on a system
            // that is already broken.
            if (_lastFailure is not null && _time.GetElapsedTime(_failedAt) < FailureCooldown)
                _lastFailure.Throw();

            try
            {
                await _runner.ApplyAsync(connection, cancellationToken);
                _ready = true;
                _lastFailure = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Only a failure that belongs to the DATABASE is cached, and the filter on this catch
                // is what ensures it. Caching one caller's cancellation would replay that client's
                // disconnect to every other caller for the whole window — including callers whose
                // connection is fine — turning a self-healing condition into five seconds of
                // guaranteed failure, and in the observability store, whose writes are swallowed by
                // design, five seconds of silently dropped telemetry.
                //
                // Review raised that the filter tests only the outermost type and would therefore
                // miss a cancellation buried inside a driver exception. Measured against Npgsql
                // rather than reasoned about: a command cancelled by its token throws
                // OperationCanceledException on the OUTSIDE, wrapping PostgresException 57014. The
                // outermost type is exactly the right thing to test, and the runner's filter excludes
                // it too. A chain-walking guard was written for this, could not be made to fail under
                // mutation, and was removed — an inert check that reads as protection is worse than
                // none. ACancelledCaller_DoesNotPoisonTheCooldownForOthers pins the behaviour.
                //
                // Server-side faults that are NOT cancellations — a command timeout, a 57P01 admin
                // shutdown — are cached, deliberately. They affect every caller equally, so bounded
                // replay is exactly what this cooldown is for.
                _lastFailure = ExceptionDispatchInfo.Capture(ex);
                _failedAt = _time.GetTimestamp();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

}

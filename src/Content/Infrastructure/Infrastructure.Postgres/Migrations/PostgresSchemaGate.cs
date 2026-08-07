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
/// </remarks>
/// <remarks>
/// <para>
/// Deliberately NOT <see cref="IDisposable"/>. It was, briefly, because it holds a
/// <see cref="SemaphoreSlim"/> — and the ceremony was immediately skipped by one of its two
/// consumers, which is the tell that it was never needed. A <see cref="SemaphoreSlim"/> only needs
/// disposing if its <c>AvailableWaitHandle</c> has been touched, which allocates the underlying
/// handle; nothing here touches it. Exporting a lifetime requirement that does not exist buys
/// nothing and gives every consumer a decision it can get wrong.
/// </para>
/// </remarks>
public sealed class PostgresSchemaGate
{
    private readonly PostgresMigrationRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    /// <summary>Initializes a new instance of the <see cref="PostgresSchemaGate"/> class.</summary>
    /// <param name="runner">The migration runner to invoke once.</param>
    public PostgresSchemaGate(PostgresMigrationRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>
    /// Initializes a new instance that builds its own runner — the shape both stores actually want.
    /// </summary>
    /// <param name="options">Ledger table and advisory lock key for this migration set.</param>
    /// <param name="scripts">The migration set, in any order.</param>
    /// <param name="logger">Logger recording which migrations were applied.</param>
    /// <remarks>
    /// Exists because both consumers were writing the same nested double-construction. The
    /// runner-taking overload stays for the test fixture, which needs to hold the runner itself.
    /// </remarks>
    public PostgresSchemaGate(
        PostgresMigrationOptions options,
        IReadOnlyList<MigrationScript> scripts,
        ILogger logger)
        : this(new PostgresMigrationRunner(options, scripts, logger))
    {
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

            await _runner.ApplyAsync(connection, cancellationToken);
            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}

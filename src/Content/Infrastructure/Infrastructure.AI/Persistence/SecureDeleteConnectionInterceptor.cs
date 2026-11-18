using System.Data.Common;
using Infrastructure.AI.RAG.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Turns SQLite secure delete on for every connection an Entity Framework context opens (#900), so deleted rows are
/// overwritten instead of left readable in the file. See <see cref="SqliteSecureDelete"/> for why and its limits.
/// </summary>
/// <remarks>
/// Attached by <c>UseSqliteWithSecureDelete</c>, the one way a context in this harness is pointed at SQLite. Stateless,
/// so a single instance serves every context. It also sets the <see cref="SqliteLockWait"/> lock wait, so a writer waits
/// for SQLite's write lock in short steps rather than the driver's 150 ms sleeps.
/// </remarks>
public sealed class SecureDeleteConnectionInterceptor : DbConnectionInterceptor
{
    /// <summary>The shared instance.</summary>
    public static SecureDeleteConnectionInterceptor Instance { get; } = new();

    private SecureDeleteConnectionInterceptor()
    {
    }

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Configure(connection);

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Configure(connection);
        return Task.CompletedTask;
    }

    private static void Configure(DbConnection connection)
    {
        SqliteSecureDelete.Apply(connection);
        SqliteLockWait.Apply(connection);
    }
}

using Infrastructure.AI.RAG.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Infrastructure.AI.Extensions;

/// <summary>Erasure housekeeping for Entity Framework contexts on SQLite (#900).</summary>
public static class SqliteDatabaseFacadeExtensions
{
    /// <summary>
    /// After an erasure's deletes, empties the database's write-ahead log so the erased rows' bytes leave the files; see
    /// <see cref="SqliteSecureDelete.TruncateWriteAheadLogAsync"/>. Does nothing for a provider other than SQLite.
    /// </summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The log was busy and was not emptied.</exception>
    public static async Task TruncateWriteAheadLogAsync(this DatabaseFacade database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!database.IsSqlite())
            return;

        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            await SqliteSecureDelete.TruncateWriteAheadLogAsync(database.GetDbConnection(), cancellationToken);
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }
}

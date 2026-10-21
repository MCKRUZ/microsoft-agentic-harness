using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Extensions;

/// <summary>Points an Entity Framework context at SQLite the way every context in this harness must be (#900).</summary>
public static class SqliteDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// <c>UseSqlite</c> plus <see cref="SecureDeleteConnectionInterceptor"/>, so deleted rows are overwritten on disk rather
    /// than left in free pages. Use it instead of <c>UseSqlite</c>; <c>EverySqliteConnectionScrubsDeletesTests</c> fails a
    /// context that does not.
    /// </summary>
    /// <param name="options">The options builder.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseSqliteWithSecureDelete(
        this DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.UseSqlite(connectionString).AddInterceptors(SecureDeleteConnectionInterceptor.Instance);
    }
}

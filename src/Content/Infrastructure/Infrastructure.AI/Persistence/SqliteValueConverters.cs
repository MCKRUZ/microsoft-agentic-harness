using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Shared EF Core <see cref="ValueConverter{TModel, TProvider}"/> instances reused
/// across the harness's SQLite-backed durable stores (prompt usage, eval dashboard,
/// and future subsystems).
/// </summary>
/// <remarks>
/// <para>
/// SQLite cannot natively <c>ORDER BY</c> a <see cref="DateTimeOffset"/> column;
/// EF Core stores it as a tuple (text + offset minutes) that doesn't compare
/// lexicographically. Round-tripping through <see cref="DateTimeOffset.UtcTicks"/>
/// keeps the column as a <c>long</c> that sorts correctly while preserving the
/// UTC instant. The offset is dropped on read — every consumer interprets the
/// recovered value as UTC, which matches how all current callers populate it.
/// </para>
/// </remarks>
public static class SqliteValueConverters
{
    /// <summary>
    /// Round-trips <see cref="DateTimeOffset"/> as <see cref="long"/> UTC ticks.
    /// Apply via <c>property.HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks)</c>.
    /// </summary>
    public static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetAsUtcTicks =
        new(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
}

/// <summary>
/// Singleton initializer that runs <see cref="DatabaseFacade.EnsureCreated"/> on its
/// supplied DbContext factory at construction time. Mirrors the
/// <c>PromptUsageSchemaInitializer</c> / <c>EvalDashboardSchemaInitializer</c>
/// pattern with a single generic base so each new SQLite subsystem doesn't
/// re-implement the same five lines.
/// </summary>
/// <typeparam name="TContext">The concrete DbContext type to initialize.</typeparam>
/// <remarks>
/// Resolved once at composition time (typically through a captive factory inside an
/// <c>AddSingleton</c> registration) so the first writer never races a missing-table
/// error. EnsureCreated is idempotent — calling it on an already-created database
/// is a no-op.
/// </remarks>
public class SchemaInitializer<TContext> where TContext : DbContext
{
    /// <summary>
    /// Initializes a new instance and ensures the underlying database exists.
    /// </summary>
    public SchemaInitializer(IDbContextFactory<TContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        using var context = contextFactory.CreateDbContext();
        context.Database.EnsureCreated();
    }
}

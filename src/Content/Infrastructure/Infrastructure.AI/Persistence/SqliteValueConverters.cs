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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// EF Core interceptor that emulates optimistic concurrency for SQLite by
/// auto-incrementing integer <c>Version</c> properties on modified entities.
/// SQLite lacks native rowversion/timestamp columns, so this interceptor
/// provides the same guarantees by treating integer version columns as
/// concurrency tokens that EF checks in the WHERE clause of UPDATE statements.
/// </summary>
public sealed class SqliteVersionInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            IncrementVersions(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            IncrementVersions(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    private static void IncrementVersions(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not EntityState.Modified)
                continue;

            var versionProp = entry.Properties
                .FirstOrDefault(p => p.Metadata.Name == "Version" && p.Metadata.ClrType == typeof(int));

            if (versionProp is null)
                continue;

            if (entry.State == EntityState.Modified)
            {
                versionProp.CurrentValue = (int)(versionProp.CurrentValue ?? 0) + 1;
            }
        }
    }
}

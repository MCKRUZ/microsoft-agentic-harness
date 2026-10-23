using Application.AI.Common.Interfaces.RAG;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Documents;

/// <summary>
/// SQLite-backed <see cref="IDocumentLoadLedger"/> (#901): one row per document, holding its id and when it was last loaded.
/// </summary>
/// <remarks>
/// Recording a load is a single upsert, so two ingests of the same document at once both succeed and the later time wins.
/// The time comes from the injected clock, never from the caller.
/// </remarks>
public sealed class EfCoreDocumentLoadLedger : IDocumentLoadLedger
{
    private readonly IDbContextFactory<DocumentLoadDbContext> _factory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="EfCoreDocumentLoadLedger"/> class.</summary>
    /// <param name="factory">Creates short-lived contexts over the load database.</param>
    /// <param name="schemaInitializer">Brings the database up to date before the first use.</param>
    /// <param name="timeProvider">Stamps each load.</param>
    public EfCoreDocumentLoadLedger(
        IDbContextFactory<DocumentLoadDbContext> factory,
        SchemaInitializer<DocumentLoadDbContext> schemaInitializer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _factory = factory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task RecordLoadedAsync(string documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var ticks = _timeProvider.GetUtcNow().UtcTicks;

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO DocumentLoads (DocumentId, LastLoadedAt) VALUES ({documentId}, {ticks})
             ON CONFLICT(DocumentId) DO UPDATE SET LastLoadedAt = MAX(LastLoadedAt, excluded.LastLoadedAt)
             """,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListLoadedBeforeAsync(
        DateTimeOffset cutoff, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        return await ctx.DocumentLoads
            .AsNoTracking()
            .Where(d => d.LastLoadedAt < cutoff)
            .OrderBy(d => d.LastLoadedAt)
            .Take(maxCount)
            .Select(d => d.DocumentId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastLoadedAsync(string documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await ctx.DocumentLoads.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == documentId, cancellationToken);
        return row?.LastLoadedAt;
    }

    /// <inheritdoc />
    public async Task<bool> ForgetAsync(
        string documentId, DateTimeOffset loadedNoLaterThan, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var deleted = await ctx.DocumentLoads
            .Where(d => d.DocumentId == documentId && d.LastLoadedAt <= loadedNoLaterThan)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }
}

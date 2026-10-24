using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using Infrastructure.AI.Extensions;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Compliance;

/// <summary>
/// SQLite-backed <see cref="IErasureJournal"/> (#907): one row per id an unfinished erasure request read, keyed by owner,
/// kind and id, stamped with the request that last recorded it.
/// </summary>
/// <remarks>
/// Every write that removes rows also truncates the write-ahead log, so the ids it removed do not linger in the log file
/// once the request that recorded them has finished.
/// </remarks>
[PersonalDataStore(
    "The erasure journal: a person's id together with the conversation, planned-task step, tool-result scope and budget ids an erasure request read for them. A request's rows are removed as soon as every store that reads them has succeeded; a request that fails keeps them, so the next request can still find what the deleted conversations and plans led to. Exempt from erasure because it is erasure's own record of unfinished work. Withheld conversation ids, and anything left by a request never retried, are removed after the configured journal retention (a year by default). Operational, so export does not apply.",
    PersonalDataCategories.Identifiers,
    PersonalDataKey.Owner,
    RetentionBasis.Configured,
    ErasureSupport.Exempt,
    ExportSupport.NotApplicable)]
public sealed class EfCoreErasureJournal : IErasureJournal
{
    private readonly IDbContextFactory<ErasureJournalDbContext> _factory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="EfCoreErasureJournal"/> class.</summary>
    /// <param name="factory">Creates short-lived contexts over the journal database.</param>
    /// <param name="schemaInitializer">Brings the database up to date before the first use.</param>
    /// <param name="timeProvider">Stamps each recorded row.</param>
    public EfCoreErasureJournal(
        IDbContextFactory<ErasureJournalDbContext> factory,
        SchemaInitializer<ErasureJournalDbContext> schemaInitializer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _factory = factory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<ErasureJournalEntries> ReadAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await ctx.Entries
            .AsNoTracking()
            .Where(e => e.OwnerId == ownerId)
            .Select(e => new { e.Kind, e.Value })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return ErasureJournalEntries.Empty;

        IReadOnlyList<string> Of(string kind) => [.. rows.Where(r => r.Kind == kind).Select(r => r.Value)];
        return new ErasureJournalEntries(
            Of(Kinds.Conversation),
            Of(Kinds.Withheld),
            new PlanRunTraces(Of(Kinds.StepConversation), Of(Kinds.ToolResultScope), Of(Kinds.RunBudgetKey)));
    }

    /// <inheritdoc />
    public async Task RecordAsync(
        string ownerId, string requestId, ErasureJournalEntries entries, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(entries);

        var ticks = _timeProvider.GetUtcNow().UtcTicks;
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await ctx.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (kind, value) in Rows(entries))
        {
            // An id already recorded is taken over: the request that read it most recently is the one that may clear it. Its
            // time is refreshed too, except a withheld id's: those are only ever removed by age, and a person re-running
            // erasure must not keep them forever.
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO ErasureJournal (OwnerId, Kind, Value, RequestId, RecordedAt)
                 VALUES ({ownerId}, {kind}, {value}, {requestId}, {ticks})
                 ON CONFLICT(OwnerId, Kind, Value) DO UPDATE SET
                     RequestId = excluded.RequestId,
                     RecordedAt = CASE WHEN Kind = {Kinds.Withheld} THEN RecordedAt ELSE excluded.RecordedAt END
                 """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HoldsForAnotherOwnerAsync(string conversationId, string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        return await ctx.Entries.AnyAsync(
            e => e.Value == conversationId && e.OwnerId != ownerId && (e.Kind == Kinds.Conversation || e.Kind == Kinds.Withheld),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string ownerId, string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var removed = await ctx.Entries
            .Where(e => e.OwnerId == ownerId && e.RequestId == requestId && e.Kind != Kinds.Withheld)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
            await ctx.Database.TruncateWriteAheadLogAsync(cancellationToken);
    }

    /// <summary>
    /// Removes every row recorded more than <paramref name="retention"/> ago, whoever it belongs to, and returns how many.
    /// Called by the journal's retention sweep.
    /// </summary>
    /// <param name="retention">How long a row is kept after it was last recorded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows removed.</returns>
    public async Task<int> PurgeOlderThanAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        var cutoff = _timeProvider.GetUtcNow() - retention;
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var removed = await ctx.Entries.Where(e => e.RecordedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
            await ctx.Database.TruncateWriteAheadLogAsync(cancellationToken);
        return removed;
    }

    private static IEnumerable<(string Kind, string Value)> Rows(ErasureJournalEntries entries) =>
        entries.ConversationIds.Select(v => (Kinds.Conversation, v))
            .Concat(entries.WithheldConversationIds.Select(v => (Kinds.Withheld, v)))
            .Concat(entries.PlanRuns.StepConversationIds.Select(v => (Kinds.StepConversation, v)))
            .Concat(entries.PlanRuns.ToolResultScopeIds.Select(v => (Kinds.ToolResultScope, v)))
            .Concat(entries.PlanRuns.RunBudgetKeys.Select(v => (Kinds.RunBudgetKey, v)))
            .Distinct();

    /// <summary>The kinds of id a row holds, as stored.</summary>
    private static class Kinds
    {
        public const string Conversation = "conversation";
        public const string Withheld = "withheld";
        public const string StepConversation = "step";
        public const string ToolResultScope = "tool-scope";
        public const string RunBudgetKey = "budget";
    }
}

using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Models.Conversations;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Conversations;

public sealed partial class EfCoreConversationStore : IIdleConversationSource
{
    /// <inheritdoc/>
    /// <remarks>
    /// No index serves this on purpose, for the reason <c>ConversationDbContext</c> gives for the budget table: the
    /// update time is rewritten by every appended message, the per-turn hot path, to speed a query that runs a few times
    /// a day over header rows only (messages are a separate table). A scan of the headers is the cheaper trade.
    /// </remarks>
    public async Task<IReadOnlyList<IdleConversation>> ListIdleAsync(
        DateTimeOffset lastActivityBefore, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Conversations
            .AsNoTracking()
            .Where(c => c.UpdatedAt < lastActivityBefore)
            .OrderBy(c => c.UpdatedAt)
            .Take(limit)
            .Select(c => new { c.Id, c.UserId, c.UpdatedAt })
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(r => new IdleConversation(r.Id, r.UserId, r.UpdatedAt));
    }

    /// <inheritdoc/>
    public async Task<bool> IsIdleAsync(
        string conversationId, DateTimeOffset lastActivityBefore, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Conversations
            .AnyAsync(c => c.Id == conversationId && c.UpdatedAt < lastActivityBefore, cancellationToken);
    }
}

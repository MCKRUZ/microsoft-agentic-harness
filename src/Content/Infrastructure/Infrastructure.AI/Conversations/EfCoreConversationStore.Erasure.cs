using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using Infrastructure.AI.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Conversations;

public sealed partial class EfCoreConversationStore : IPersonalDataEraser, IPersonalDataScrubber
{
    /// <inheritdoc/>
    public string ErasureStoreName => "conversations";

    /// <inheritdoc/>
    /// <remarks>Last: the stores reached through a conversation's id need it to still exist.</remarks>
    public ErasureStage ErasureStage => ErasureStage.Conversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.NamedConversations;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Removes exactly the conversations listed in <see cref="ErasureSubject.ConversationIds"/> — the ones whose dependent
    /// records were cleared earlier in the request — and only while they still belong to the person, matched on
    /// <see cref="ErasureSubject.OwnerId"/>, the as-issued form <c>CreateAsync</c> stores. A conversation started after
    /// that list was read is left for a second request, because what it produced was never cleared. Messages go with
    /// each conversation through the foreign-key cascade.
    /// </para>
    /// </remarks>
    public async Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.ConversationIds.Count == 0)
            return 0;

        var ids = subject.ConversationIds.ToList();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Conversations
            .Where(c => ids.Contains(c.Id) && c.UserId == subject.OwnerId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ScrubAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.TruncateWriteAheadLogAsync(cancellationToken);
    }
}

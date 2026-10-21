using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Conversations;

public sealed partial class FileSystemConversationStore : IPersonalDataEraser
{
    /// <inheritdoc/>
    public string ErasureStoreName => "conversations";

    /// <inheritdoc/>
    /// <remarks>Last: the stores reached through a conversation's id need it to still exist.</remarks>
    public ErasureStage ErasureStage => ErasureStage.Conversations;

    /// <inheritdoc/>
    /// <remarks>
    /// Removes exactly the conversations listed in <see cref="ErasureSubject.ConversationIds"/> — the ones whose dependent
    /// records were cleared earlier in the request; one started after that list was read is left for a second request.
    /// Each goes through <see cref="DeleteAsync"/> as its owner, so the per-file lock and the ownership check apply
    /// exactly as they do to a single delete.
    /// </remarks>
    public async Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var removed = 0;
        foreach (var conversationId in subject.ConversationIds)
        {
            if (await DeleteAsync(conversationId, subject.OwnerId, cancellationToken))
                removed++;
        }

        return removed;
    }
}

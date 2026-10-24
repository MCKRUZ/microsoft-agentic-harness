using System.Text.Json;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Services;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Conversations;

public sealed partial class FileSystemConversationStore : IPersonalDataEraser, IConversationIdSource
{
    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Every conversation is a file, so every file is read, but only its id and owner are parsed, and nothing is written
    /// back (the listing <see cref="ListAsync"/> serves also migrates old records; erasure has no reason to). Each file
    /// is read under its own lock, so a read never lands mid-write.
    /// </para>
    /// <para>
    /// A file that cannot be read or parsed is counted, never skipped: <see cref="ListAsync"/> logs and moves on, which
    /// for erasure would leave a conversation the person may own, and everything it leads to, behind a receipt that read
    /// <c>Full</c>. Its name is not logged here: it is a conversation id.
    /// </para>
    /// </remarks>
    public async Task<ConversationIdListing> ListConversationIdsAsync(string ownerId, CancellationToken cancellationToken)
    {
        ConversationOwnership.RequireCallerId(ownerId);

        var ids = new List<string>();
        var unreadable = 0;
        foreach (var file in Directory.GetFiles(_basePath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (await _locks.AcquireAsync(file, cancellationToken))
            {
                // Deleted since the directory was listed.
                if (!File.Exists(file))
                    continue;

                switch (await ReadOwnershipAsync(file, cancellationToken))
                {
                    case null:
                        unreadable++;
                        break;
                    case { } header when header.UserId == ownerId:
                        ids.Add(header.Id);
                        break;
                }
            }
        }

        if (unreadable > 0)
            _logger.LogWarning("Listing conversations for erasure: {Unreadable} conversation file(s) could not be read.", unreadable);

        return new ConversationIdListing(ids, unreadable);
    }

    /// <summary>The id and owner of the conversation in <paramref name="file"/>, or <see langword="null"/> when it cannot be read.</summary>
    private static async Task<StoredOwner?> ReadOwnershipAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken);
            var header = JsonSerializer.Deserialize<StoredOwner>(json, ConversationJson.Options);

            // The file's name is the conversation's id; the id inside must agree. One that names another conversation
            // would lead the stores keyed by conversation id, which check no owner, to that other conversation's records.
            return header is { Id.Length: > 0, UserId.Length: > 0 }
                && string.Equals(header.Id, Path.GetFileNameWithoutExtension(file), StringComparison.Ordinal)
                    ? header
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The two fields of a stored conversation that erasure reads.</summary>
    private sealed record StoredOwner(string Id, string UserId);

    /// <inheritdoc/>
    public string ErasureStoreName => "conversations";

    /// <inheritdoc/>
    /// <remarks>Last: the stores reached through a conversation's id need it to still exist.</remarks>
    public ErasureStage ErasureStage => ErasureStage.Conversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.NamedConversations;

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

using System.Text.Json;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Models.Conversations;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Conversations;

public sealed partial class FileSystemConversationStore : IIdleConversationSource
{
    /// <inheritdoc/>
    /// <remarks>
    /// Reads every conversation file, each under its own lock as <see cref="ListAsync"/> does, because the update time is
    /// inside the file. A file that cannot be read is skipped and logged, never treated as idle.
    /// </remarks>
    public async Task<IReadOnlyList<IdleConversation>> ListIdleAsync(
        DateTimeOffset lastActivityBefore, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var idle = new List<IdleConversation>();
        foreach (var file in Directory.GetFiles(_basePath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await TryReadAsync(file, cancellationToken);
            if (record is not null && record.UpdatedAt < lastActivityBefore)
                idle.Add(new IdleConversation(record.Id, record.UserId, record.UpdatedAt));
        }

        return idle.OrderBy(c => c.LastActivityAt).Take(limit).ToList();
    }

    /// <inheritdoc/>
    public async Task<bool> IsIdleAsync(
        string conversationId, DateTimeOffset lastActivityBefore, CancellationToken cancellationToken)
    {
        var record = await TryReadAsync(ResolveAndValidatePath(conversationId), cancellationToken);
        return record is not null && record.UpdatedAt < lastActivityBefore;
    }

    /// <summary>Reads one conversation file under its lock, or <see langword="null"/> when it is gone or unreadable.</summary>
    private async Task<ConversationRecord?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        using (await _locks.AcquireAsync(path, cancellationToken))
        {
            if (!File.Exists(path))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                return JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Failed to read conversation file {File} for retention; skipping.", path);
                return null;
            }
        }
    }
}

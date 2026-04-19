using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;

namespace Presentation.AgentHub.Services;

/// <summary>
/// File-system-backed conversation store. Each <see cref="ConversationRecord"/> is stored as a
/// JSON file at <c>{ConversationsPath}/{conversationId}.json</c>.
///
/// Thread safety: a single <see cref="SemaphoreSlim"/> serializes all file I/O. This is
/// intentionally simple for POC scale. A production implementation should use
/// per-conversation-id locking (e.g., AsyncKeyedLock) to allow concurrent operations
/// across different conversations.
///
/// Atomic writes: all writes go to a <c>.tmp</c> file first, then <see cref="File.Move"/> with
/// <c>overwrite: true</c>. This prevents partial-write corruption if the process exits mid-write.
///
/// Path safety: the constructor resolves <c>ConversationsPath</c> to an absolute path.
/// Any operation whose computed file path does not start with this base path throws
/// <see cref="ArgumentException"/>, preventing path-traversal attacks via crafted conversation IDs.
/// </summary>
public sealed class FileSystemConversationStore : IConversationStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly string _basePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<FileSystemConversationStore> _logger;

    /// <summary>
    /// Initialises the store, resolving <see cref="AgentHubConfig.ConversationsPath"/> to an
    /// absolute path and creating the directory if it does not yet exist.
    /// </summary>
    public FileSystemConversationStore(
        IOptions<AgentHubConfig> config,
        ILogger<FileSystemConversationStore> logger)
    {
        _basePath = Path.GetFullPath(config.Value.ConversationsPath);
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct = default)
    {
        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var record = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
            if (record is null) return null;

            // Migrate legacy records whose messages predate the Id column by backfilling
            // a Guid per message and persisting the result. Subsequent loads will skip this path.
            var migrated = MigrateMissingIds(record);
            if (migrated is not null)
            {
                await WriteAtomicLockedAsync(path, migrated, ct);
                return migrated;
            }
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var files = Directory.GetFiles(_basePath, "*.json");
            var results = new List<ConversationRecord>();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    var record = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
                    if (record is null || record.UserId != userId)
                        continue;
                    var migrated = MigrateMissingIds(record);
                    if (migrated is not null)
                    {
                        await WriteAtomicLockedAsync(file, migrated, ct);
                        results.Add(migrated);
                    }
                    else
                    {
                        results.Add(record);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize conversation file {File}; skipping.", file);
                }
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord> CreateAsync(string agentName, string userId, string? conversationId = null, CancellationToken ct = default)
    {
        var id = !string.IsNullOrWhiteSpace(conversationId) ? conversationId : Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var record = new ConversationRecord(
            Id: id,
            AgentName: agentName,
            UserId: userId,
            CreatedAt: now,
            UpdatedAt: now,
            Messages: []);

        var path = ResolveAndValidatePath(id);
        await WriteAtomicAsync(path, record, ct);

        _logger.LogDebug("Created conversation {ConversationId} for user {UserId}.", id, userId);
        return record;
    }

    /// <inheritdoc/>
    public async Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct = default)
    {
        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"Conversation '{conversationId}' does not exist.");

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions)
                ?? throw new InvalidOperationException($"Conversation '{conversationId}' could not be deserialized.");

            var derivedTitle = existing.Title
                ?? (message.Role == MessageRole.User
                    ? ConversationRecordTitleDerivation.Derive(message.Content)
                    : null);

            var updated = existing with
            {
                Messages = [..existing.Messages, message],
                UpdatedAt = DateTimeOffset.UtcNow,
                Title = derivedTitle,
            };

            await WriteAtomicLockedAsync(path, updated, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> TruncateFromMessageAsync(
        string conversationId,
        Guid messageId,
        CancellationToken ct = default)
    {
        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
            if (existing is null) return null;

            var idx = IndexOfMessage(existing.Messages, messageId);
            if (idx < 0) return existing;

            var truncated = existing with
            {
                Messages = [..existing.Messages.Take(idx)],
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await WriteAtomicLockedAsync(path, truncated, ct);
            return truncated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> UpdateSettingsAsync(
        string conversationId,
        ConversationSettings settings,
        CancellationToken ct = default)
    {
        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
            if (existing is null) return null;

            var updated = existing with
            {
                Settings = settings,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await WriteAtomicLockedAsync(path, updated, ct);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
        string conversationId,
        int maxMessages,
        CancellationToken ct = default)
    {
        var record = await GetAsync(conversationId, ct);
        if (record is null)
            return null;

        var messages = record.Messages;
        if (messages.Count <= maxMessages)
            return messages;

        return messages.Skip(messages.Count - maxMessages).ToList();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a migrated copy of <paramref name="record"/> if any message had an empty Id;
    /// returns <c>null</c> when no migration was needed (caller should use the original).
    /// </summary>
    private static ConversationRecord? MigrateMissingIds(ConversationRecord record)
    {
        if (record.Messages.Count == 0 || !record.Messages.Any(m => m.Id == Guid.Empty))
            return null;

        var migratedMessages = record.Messages
            .Select(m => m.Id == Guid.Empty ? m with { Id = Guid.NewGuid() } : m)
            .ToList();

        return record with { Messages = migratedMessages };
    }

    private static int IndexOfMessage(IReadOnlyList<ConversationMessage> messages, Guid messageId)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id == messageId) return i;
        }
        return -1;
    }

    private string ResolveAndValidatePath(string conversationId)
    {
        // Resolve the full path and verify it stays within _basePath to prevent
        // path-traversal attacks via crafted conversation IDs like "../evil".
        var fullPath = Path.GetFullPath(Path.Combine(_basePath, $"{conversationId}.json"));
        if (!fullPath.StartsWith(_basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Conversation ID '{conversationId}' resolves outside the allowed base path.",
                nameof(conversationId));
        }
        return fullPath;
    }

    /// <summary>
    /// Writes <paramref name="record"/> atomically (tmp → move) while holding <see cref="_lock"/>.
    /// Call this only from <see cref="CreateAsync"/> where the lock is not yet held.
    /// </summary>
    private async Task WriteAtomicAsync(string targetPath, ConversationRecord record, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WriteAtomicLockedAsync(targetPath, record, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Writes <paramref name="record"/> atomically (tmp → move). Must be called while the
    /// caller already holds <see cref="_lock"/>.
    /// </summary>
    private static async Task WriteAtomicLockedAsync(string targetPath, ConversationRecord record, CancellationToken ct)
    {
        var tmpPath = targetPath + ".tmp";
        var json = JsonSerializer.Serialize(record, _jsonOptions);
        await File.WriteAllTextAsync(tmpPath, json, ct);
        File.Move(tmpPath, targetPath, overwrite: true);
    }
}

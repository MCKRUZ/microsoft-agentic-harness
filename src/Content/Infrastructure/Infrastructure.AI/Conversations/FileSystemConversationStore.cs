using System.Text.Json;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Domain.Common.Config.AI.Conversations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// File-system-backed conversation store. Each <see cref="ConversationRecord"/> is stored as a
/// JSON file at <c>{ConversationsPath}/{conversationId}.json</c>.
///
/// Thread safety: a single <see cref="SemaphoreSlim"/> serializes all file I/O. This is
/// intentionally simple for POC scale. A production implementation should use
/// per-conversation-id locking (e.g., AsyncKeyedLock) to allow concurrent operations
/// across different conversations.
///
/// <para>
/// <strong>Single-process only.</strong> That semaphore is in-process, so it serializes nothing
/// between hosts. Two processes sharing one <c>ConversationsPath</c> can interleave writes to the
/// shared <c>.tmp</c> staging file and move a torn record into place. This store is therefore fit
/// for one host at a time — see <see cref="Domain.Common.Config.AI.Conversations.ConversationsConfig.ConversationsPath"/>.
/// </para>
///
/// Atomic writes: all writes go to a <c>.tmp</c> file first, then <see cref="System.IO.File.Move(string, string, bool)"/> with
/// <c>overwrite: true</c>. This prevents partial-write corruption if the process exits mid-write.
///
/// Path safety: the constructor resolves <c>ConversationsPath</c> to an absolute path.
/// Any operation whose computed file path does not start with this base path throws
/// <see cref="ArgumentException"/>, preventing path-traversal attacks via crafted conversation IDs.
/// </summary>
public sealed class FileSystemConversationStore : IConversationStore
{
    private readonly string _basePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileSystemConversationStore> _logger;

    /// <summary>
    /// Initialises the store, resolving <see cref="ConversationsConfig.ConversationsPath"/> to an
    /// absolute path and creating the directory if it does not yet exist.
    /// </summary>
    /// <param name="config">Supplies the conversations directory.</param>
    /// <param name="timeProvider">
    /// Clock for <c>CreatedAt</c>/<c>UpdatedAt</c> stamps. Injected rather than read from
    /// <see cref="DateTimeOffset.UtcNow"/> so that both implementations of
    /// <see cref="IConversationStore"/> answer to the same clock — a host that supplies its own
    /// would otherwise get deterministic timestamps from one provider and wall-clock from the other,
    /// a divergence nothing in the contract suite could see.
    /// </param>
    /// <param name="logger">Diagnostic logger.</param>
    public FileSystemConversationStore(
        IOptions<ConversationsConfig> config,
        TimeProvider timeProvider,
        ILogger<FileSystemConversationStore> logger)
    {
        _basePath = Path.GetFullPath(config.Value.ConversationsPath);
        _timeProvider = timeProvider;
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
            var record = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
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
                    var record = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
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
        var now = _timeProvider.GetUtcNow();
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
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options)
                ?? throw new InvalidOperationException($"Conversation '{conversationId}' could not be deserialized.");

            // Message ids are client-supplied, so a replayed or double-submitted turn arrives with an
            // id the conversation already holds. Rejected rather than appended: a transcript with two
            // rows sharing one id makes a retry's cut point arbitrary, since truncation resolves an id
            // to the first match. The SQLite store gets the same rejection from a unique index.
            if (message.Id != Guid.Empty && existing.Messages.Any(m => m.Id == message.Id))
            {
                throw new InvalidOperationException(
                    $"Message '{message.Id}' already exists in conversation '{conversationId}'.");
            }

            var derivedTitle = existing.Title
                ?? (message.Role == MessageRole.User
                    ? ConversationRecordTitleDerivation.Derive(message.Content)
                    : null);

            var updated = existing with
            {
                Messages = [..existing.Messages, message],
                UpdatedAt = _timeProvider.GetUtcNow(),
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
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            if (existing is null) return null;

            var idx = IndexOfMessage(existing.Messages, messageId);
            if (idx < 0) return existing;

            var truncated = existing with
            {
                Messages = [..existing.Messages.Take(idx)],
                UpdatedAt = _timeProvider.GetUtcNow(),
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
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            if (existing is null) return null;

            var updated = existing with
            {
                Settings = settings,
                UpdatedAt = _timeProvider.GetUtcNow(),
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
    public async Task<ConversationRecord?> UpdateTelemetryAsync(
        string conversationId,
        Guid observabilitySessionId,
        TelemetryAccumulator telemetry,
        CancellationToken ct = default)
    {
        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            if (existing is null) return null;

            var updated = existing with
            {
                ObservabilitySessionId = observabilitySessionId,
                Telemetry = telemetry,
                UpdatedAt = _timeProvider.GetUtcNow(),
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

        // Exclude empty-content messages (an inline-widget message carries its payload in WidgetSpec, not
        // text, so it is not model-relevant). Filtering before the window keeps the cap counting real
        // conversational turns — otherwise widget-heavy conversations would silently starve the model of
        // context as the widgets consume window slots then get dropped at mapping time.
        var messages = record.Messages.Where(m => !string.IsNullOrEmpty(m.Content)).ToList();
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
    /// Retries <see cref="System.IO.File.Move(string, string, bool)"/> up to 3 times on <see cref="UnauthorizedAccessException"/>
    /// to tolerate transient file locks from OneDrive, antivirus, or Windows Search.
    /// </summary>
    private static async Task WriteAtomicLockedAsync(string targetPath, ConversationRecord record, CancellationToken ct)
    {
        var tmpPath = targetPath + ".tmp";
        var json = JsonSerializer.Serialize(record, ConversationJson.Options);
        await File.WriteAllTextAsync(tmpPath, json, ct);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmpPath, targetPath, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                await Task.Delay(50 * (attempt + 1), ct);
            }
        }
    }
}

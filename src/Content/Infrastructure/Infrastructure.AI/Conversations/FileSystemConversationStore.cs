using System.Text.Json;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services;
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
    public async Task<ConversationRecord?> GetAsync(string conversationId, string callerId, CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var record = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            if (record is null) return null;

            RequireOwner(conversationId, callerId, record.UserId);

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
        ConversationOwnership.RequireCallerId(userId);

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
        ConversationOwnership.RequireCallerId(userId);

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

        // The ownership check and the write share one lock acquisition on purpose. Writing the
        // record's file overwrites whatever was there, so a caller naming an existing id replaces
        // that conversation outright — the one create path that can destroy a transcript. Checking
        // under a separate acquisition would leave a window in which the conversation being replaced
        // is not the one whose owner was approved.
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                var existingJson = await File.ReadAllTextAsync(path, ct);
                var existing = JsonSerializer.Deserialize<ConversationRecord>(existingJson, ConversationJson.Options);
                if (existing is not null)
                    RequireOwner(id, userId, existing.UserId);
            }

            await WriteAtomicLockedAsync(path, record, ct);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogDebug("Created conversation {ConversationId} for user {UserId}.", id, userId);
        return record;
    }

    /// <inheritdoc/>
    public async Task AppendMessageAsync(string conversationId, string callerId, ConversationMessage message, CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"Conversation '{conversationId}' does not exist.");

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options)
                ?? throw new InvalidOperationException($"Conversation '{conversationId}' could not be deserialized.");

            RequireOwner(conversationId, callerId, existing.UserId);

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
    public async Task<bool> DeleteAsync(string conversationId, string callerId, CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return false;

            // Unlike the SQLite store, which puts the owner in the DELETE's WHERE clause, this one
            // has to read the record to learn who owns it. Both reads happen under the same lock, so
            // nothing can change hands in between; that is the property SQLite gets from the statement.
            var json = await File.ReadAllTextAsync(path, ct);

            // A record too corrupt to name an owner is deleted, as it was before ownership moved here.
            // Refusing instead would make it permanently undeletable through the API, and would buy
            // nothing: corrupting the file first requires write access to the directory, and anyone
            // holding that can delete it outright without asking this store.
            //
            // The catch is what makes that true rather than merely intended. Deserialize returns null
            // only for the JSON literal `null`; the corruption that actually happens — truncated or
            // malformed text — throws, and an uncaught throw here produces exactly the undeletable
            // record this paragraph rules out. ListAsync already treats a bad file as an expected
            // condition, so the store agrees elsewhere that these occur.
            ConversationRecord? existing;
            try
            {
                existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Conversation {ConversationId} could not be deserialized; deleting it unverified.",
                    conversationId);
                existing = null;
            }

            if (existing is not null)
                RequireOwner(conversationId, callerId, existing.UserId);

            File.Delete(path);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> TruncateFromMessageAsync(
        string conversationId,
        string callerId,
        Guid messageId,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            if (existing is null) return null;

            RequireOwner(conversationId, callerId, existing.UserId);

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
        string callerId,
        ConversationSettings settings,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            if (existing is null) return null;

            RequireOwner(conversationId, callerId, existing.UserId);

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
        string callerId,
        Guid observabilitySessionId,
        TelemetryAccumulator telemetry,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
            if (existing is null) return null;

            RequireOwner(conversationId, callerId, existing.UserId);

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
        string callerId,
        int maxMessages,
        CancellationToken ct = default)
    {
        // Ownership is enforced by the read itself — no separate check to keep in step with GetAsync's.
        var record = await GetAsync(conversationId, callerId, ct);
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

    /// <summary>Applies the shared ownership rule. See <see cref="ConversationOwnership"/>.</summary>
    private void RequireOwner(string conversationId, string callerId, string ownerId) =>
        ConversationOwnership.RequireOwner(_logger, conversationId, callerId, ownerId);

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
            // Only File.Move sits inside this try. That boundary is load-bearing now that an
            // ownership refusal is also an UnauthorizedAccessException: widening the try to cover an
            // ownership check would retry a denial three times and then rethrow it as a file error.
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                await Task.Delay(50 * (attempt + 1), ct);
            }
        }
    }
}

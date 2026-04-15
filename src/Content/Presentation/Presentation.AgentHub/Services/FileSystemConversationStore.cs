using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Models;

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
            return JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
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
                    if (record?.UserId == userId)
                        results.Add(record);
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
    public async Task<ConversationRecord> CreateAsync(string agentName, string userId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
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

            var updated = existing with
            {
                Messages = [..existing.Messages, message],
                UpdatedAt = DateTimeOffset.UtcNow,
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

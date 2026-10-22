using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Compliance;
using Domain.AI.Orchestration;
using Domain.Common.Config;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Append-only JSONL file store for <see cref="DelegationRecord"/> instances.
/// Each supervisor gets a subdirectory; each session gets one JSONL file.
/// Thread-safe via per-file semaphore locks with bounded LRU eviction.
/// </summary>
/// <remarks>
/// <para>
/// File layout:
/// <code>
/// {DelegationStoragePath}/
///   {supervisorId}/
///     {sessionTimestamp}.jsonl
/// </code>
/// </para>
/// <para>
/// Records are immutable snapshots — state transitions append new records rather
/// than mutating existing ones. Deduplication selects the last record per
/// <see cref="DelegationRecord.DelegationId"/>, which represents the latest state.
/// </para>
/// </remarks>
[PersonalDataStore(
    "Append-only JSONL of subagent delegations under {Subagent:DelegationStoragePath} (default .agent-sessions/delegations), holding model-written task descriptions and failure reasons. Records carry no user or conversation id; a session file nothing has written to for Subagent:DelegationRetention:RetainFor (default 90 days) is deleted.",
    PersonalDataCategories.ConversationContent | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.Configured,
    ErasureSupport.NotSupported,
    ExportSupport.NotSupported)]
public sealed class JsonlDelegationStore : IDelegationStore, IStoreRetentionPeriod, IDisposable
{
    /// <inheritdoc />
    public TimeSpan? CurrentRetentionPeriod =>
        Retention is { Enabled: true } retention ? retention.RetainFor : null;

    private RetentionPeriodConfig Retention => _config.CurrentValue.AI.Orchestration.Subagent.DelegationRetention;

    /// <summary>
    /// Deletes every session file nothing has written to since <paramref name="cutoff"/> (#901), then any supervisor
    /// folder left empty.
    /// </summary>
    /// <remarks>
    /// Each file is checked again under its own lock before it is deleted, so a record appended in between keeps it. A
    /// file this process still appends to may be deleted when it has been idle that long; the next record recreates it.
    /// </remarks>
    /// <param name="cutoff">Files last written before this instant are deleted.</param>
    /// <param name="ct">Stops between files.</param>
    /// <returns>How many files were deleted.</returns>
    public async Task<int> DeleteNotWrittenSinceAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var file in GetAllSessionFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime)
                continue;

            var semaphore = GetFileLock(file);
            await semaphore.WaitAsync(ct);
            try
            {
                if (File.Exists(file) && File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        foreach (var directory in Directory.Exists(_basePath) ? Directory.GetDirectories(_basePath) : [])
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }

        return deleted;
    }

    private const int MaxLockCacheEntries = 100;

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _basePath;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<JsonlDelegationStore> _logger;
    private readonly ConcurrentDictionary<string, string> _sessionFiles = new();
    private readonly ConcurrentDictionary<string, (SemaphoreSlim Semaphore, long LastUsed)> _fileLocks = new();

    /// <summary>
    /// Initializes a new instance of <see cref="JsonlDelegationStore"/>.
    /// </summary>
    /// <param name="config">Application configuration providing the delegation storage path.</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    public JsonlDelegationStore(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlDelegationStore> logger)
    {
        _basePath = config.CurrentValue.AI.Orchestration.Subagent.DelegationStoragePath;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AppendAsync(DelegationRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var path = ResolveSessionFile(record.SupervisorId);
        var line = JsonSerializer.Serialize(record, SerializeOptions) + "\n";
        var semaphore = GetFileLock(path);

        await semaphore.WaitAsync(ct);
        try
        {
            EnsureDirectoryExists(path);
            await File.AppendAllTextAsync(path, line, ct);
        }
        finally
        {
            semaphore.Release();
        }

        _logger.LogDebug(
            "Appended delegation {DelegationId} state={State} to {FilePath}",
            record.DelegationId, record.State, path);
    }

    /// <inheritdoc />
    public async Task<DelegationRecord?> GetByIdAsync(Guid delegationId, CancellationToken ct = default)
    {
        var allFiles = GetAllSessionFiles();

        foreach (var file in allFiles)
        {
            var records = await ReadAllRecordsAsync(file, ct);
            var match = records
                .Where(r => r.DelegationId == delegationId)
                .LastOrDefault();

            if (match is not null)
                return match;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DelegationRecord>> GetBySessionAsync(
        string supervisorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supervisorId);

        var files = GetSupervisorFiles(supervisorId);
        var all = new List<DelegationRecord>();

        foreach (var file in files)
        {
            all.AddRange(await ReadAllRecordsAsync(file, ct));
        }

        return Deduplicate(all);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DelegationRecord>> GetByParentAsync(
        Guid parentDelegationId,
        CancellationToken ct = default)
    {
        var allFiles = GetAllSessionFiles();
        var all = new List<DelegationRecord>();

        foreach (var file in allFiles)
        {
            var records = await ReadAllRecordsAsync(file, ct);
            all.AddRange(records.Where(r => r.ParentDelegationId == parentDelegationId));
        }

        return Deduplicate(all);
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose()
    {
        foreach (var entry in _fileLocks.Values)
            entry.Semaphore.Dispose();

        _fileLocks.Clear();
    }

    /// <summary>
    /// Sanitizes a supervisor ID to contain only letters, digits, hyphens, and underscores.
    /// Prevents path traversal via malicious IDs containing ".." or directory separators.
    /// </summary>
    private static string SanitizeSupervisorId(string supervisorId)
    {
        var sanitized = string.Concat(supervisorId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (sanitized.Length == 0)
            throw new ArgumentException("SupervisorId must contain at least one alphanumeric character.", nameof(supervisorId));
        return sanitized;
    }

    /// <summary>
    /// Resolves or creates the session JSONL file path for a supervisor.
    /// On first access, creates the supervisor directory and generates a
    /// timestamped filename.
    /// </summary>
    private string ResolveSessionFile(string supervisorId)
    {
        return _sessionFiles.GetOrAdd(supervisorId, id =>
        {
            var dir = Path.Combine(_basePath, SanitizeSupervisorId(id));
            OwnerOnlyDirectoryHelper.Create(dir, _logger); // #640

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss'Z'");
            var filePath = Path.Combine(dir, $"{timestamp}.jsonl");

            _logger.LogInformation(
                "Created session file for supervisor {SupervisorId}: {FilePath}",
                id, filePath);

            return filePath;
        });
    }

    /// <summary>
    /// Returns the per-file semaphore, creating one if needed. Evicts the
    /// least-recently-used entry when the cache exceeds <see cref="MaxLockCacheEntries"/>.
    /// </summary>
    private SemaphoreSlim GetFileLock(string path)
    {
        if (_fileLocks.TryGetValue(path, out var existing))
        {
            _fileLocks[path] = (existing.Semaphore, Environment.TickCount64);
            return existing.Semaphore;
        }

        if (_fileLocks.Count >= MaxLockCacheEntries)
            EvictOldestLock();

        var semaphore = new SemaphoreSlim(1, 1);
        var entry = _fileLocks.GetOrAdd(path, (semaphore, Environment.TickCount64));

        // Another thread may have won the race — dispose ours if so
        if (!ReferenceEquals(entry.Semaphore, semaphore))
            semaphore.Dispose();

        return entry.Semaphore;
    }

    /// <summary>
    /// Removes and disposes the lock entry with the smallest LastUsed timestamp.
    /// </summary>
    private void EvictOldestLock()
    {
        string? oldestKey = null;
        long oldestTick = long.MaxValue;

        foreach (var kvp in _fileLocks)
        {
            if (kvp.Value.LastUsed < oldestTick)
            {
                oldestTick = kvp.Value.LastUsed;
                oldestKey = kvp.Key;
            }
        }

        if (oldestKey is not null && _fileLocks.TryRemove(oldestKey, out var removed))
            removed.Semaphore.Dispose();
    }

    /// <summary>
    /// Reads all delegation records from a JSONL file, skipping blank and
    /// corrupted lines. Acquires the file semaphore for read consistency.
    /// </summary>
    private async Task<List<DelegationRecord>> ReadAllRecordsAsync(
        string path,
        CancellationToken ct)
    {
        var records = new List<DelegationRecord>();

        if (!File.Exists(path))
            return records;

        var semaphore = GetFileLock(path);
        await semaphore.WaitAsync(ct);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var lineNumber = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineNumber++;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var record = JsonSerializer.Deserialize<DelegationRecord>(line, DeserializeOptions);
                    if (record is not null)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    _logger.LogWarning(
                        "Skipped corrupted delegation record at {FilePath}:{LineNumber}",
                        path, lineNumber);
                }
            }
        }
        finally
        {
            semaphore.Release();
        }

        return records;
    }

    /// <summary>
    /// Returns all JSONL files across every supervisor directory.
    /// </summary>
    private IReadOnlyList<string> GetAllSessionFiles()
    {
        if (!Directory.Exists(_basePath))
            return [];

        return Directory.GetFiles(_basePath, "*.jsonl", SearchOption.AllDirectories);
    }

    /// <summary>
    /// Returns all JSONL files for a specific supervisor.
    /// </summary>
    private IReadOnlyList<string> GetSupervisorFiles(string supervisorId)
    {
        var dir = Path.Combine(_basePath, SanitizeSupervisorId(supervisorId));

        if (!Directory.Exists(dir))
            return [];

        return Directory.GetFiles(dir, "*.jsonl");
    }

    /// <summary>
    /// Deduplicates records by <see cref="DelegationRecord.DelegationId"/>,
    /// keeping the last occurrence (latest state snapshot).
    /// </summary>
    private static IReadOnlyList<DelegationRecord> Deduplicate(List<DelegationRecord> records)
    {
        return records
            .GroupBy(r => r.DelegationId)
            .Select(g => g.Last())
            .ToList();
    }

    /// <summary>
    /// Ensures the parent directory for the given file path exists.
    /// </summary>
    private void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            OwnerOnlyDirectoryHelper.Create(dir, _logger); // #640
    }
}

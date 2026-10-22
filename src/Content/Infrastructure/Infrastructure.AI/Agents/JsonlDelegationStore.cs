using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Orchestration;
using Domain.Common.Config;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;
using Application.Common.Interfaces.Compliance;

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
    "Append-only JSONL of subagent delegations under {Subagent:DelegationStoragePath} (default .agent-sessions/delegations), holding model-written task descriptions and failure reasons. Records carry no user or conversation id. Each supervisor writes one file per UTC day; a file nothing has written to for Subagent:DelegationRetention:RetainFor (default 90 days) is deleted.",
    PersonalDataCategories.ConversationContent | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.Configured,
    ErasureSupport.NotSupported,
    ExportSupport.NotSupported)]
public sealed class JsonlDelegationStore : IDelegationStore, IStoreRetentionPeriod, IDisposable
{
    /// <inheritdoc />
    /// <remarks>
    /// A session file takes records for one UTC day, so a record is gone at most a day after the period: its file's last
    /// write is no later than the end of the day it was written. A period below the sweep's floor is refused by the sweep,
    /// so it is reported as no period at all rather than one that is not enforced.
    /// </remarks>
    public TimeSpan? CurrentRetentionPeriod =>
        Retention is { Enabled: true } retention && retention.RetainFor >= RetentionPeriodConfig.MinimumRetainFor
            ? retention.RetainFor + TimeSpan.FromDays(1)
            : null;

    private RetentionPeriodConfig Retention => _config.CurrentValue.AI.Orchestration.Subagent.DelegationRetention;

    /// <summary>The names this store gives session files: <c>{yyyyMMddTHHmmssZ}.jsonl</c>. Nothing else is ever deleted.</summary>
    private static readonly System.Text.RegularExpressions.Regex SessionFileName =
        new(@"^\d{8}T\d{6}Z\.jsonl$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Deletes every session file nothing has written to since <paramref name="cutoff"/> (#901).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only files this store names, exactly one folder below the storage path, and never through a link: a storage path
    /// pointed at a parent of other JSONL logs (the hash-chained audit trails among them) must not let this delete them.
    /// </para>
    /// <para>
    /// Each file is checked again under its own lock before it is deleted, so a record appended in between keeps it. The
    /// supervisor folders are left in place; their names are fixed and say nothing about anyone.
    /// </para>
    /// </remarks>
    /// <param name="cutoff">Files last written before this instant are deleted.</param>
    /// <param name="ct">Stops between files.</param>
    /// <returns>How many files were deleted.</returns>
    public async Task<int> DeleteNotWrittenSinceAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var file in OwnSessionFiles())
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

        return deleted;
    }

    /// <summary>The session files this store wrote: named by it, one folder deep, links skipped.</summary>
    private IEnumerable<string> OwnSessionFiles()
    {
        if (!Directory.Exists(_basePath))
            return [];

        var noLinks = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateDirectories(_basePath, "*", noLinks)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.jsonl", noLinks))
            .Where(file => SessionFileName.IsMatch(Path.GetFileName(file)))
            .ToList();
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
    /// <param name="timeProvider">The clock that decides which day's file a record goes to; the system clock when omitted.</param>
    public JsonlDelegationStore(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlDelegationStore> logger,
        TimeProvider? timeProvider = null)
    {
        _basePath = config.CurrentValue.AI.Orchestration.Subagent.DelegationStoragePath;
        _config = config;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private readonly TimeProvider _timeProvider;

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
        // One file per supervisor per UTC day (#901): retention deletes a file by its last write, so a file a long-running
        // process kept appending to would never age and its oldest records would outlive the period.
        var now = _timeProvider.GetUtcNow();
        var key = $"{supervisorId}|{now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}";
        return _sessionFiles.GetOrAdd(key, _ =>
        {
            // Earlier days' entries for this supervisor are no longer written to.
            foreach (var stale in _sessionFiles.Keys.Where(k => k.StartsWith(supervisorId + "|", StringComparison.Ordinal) && k != key))
                _sessionFiles.TryRemove(stale, out string? _);

            var id = supervisorId;
            var dir = Path.Combine(_basePath, SanitizeSupervisorId(id));
            OwnerOnlyDirectoryHelper.Create(dir, _logger); // #640

            var timestamp = now.ToString("yyyyMMddTHHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
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

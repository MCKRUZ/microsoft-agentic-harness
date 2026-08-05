using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Filesystem-backed implementation of <see cref="IHarnessCandidateRepository"/>.
/// Stores each candidate as an atomic JSON file under
/// <c>{TraceDirectoryRoot}/optimizations/{runId}/candidates/{candidateId}/candidate.json</c>
/// and maintains a lightweight <c>index.jsonl</c> per run for O(n) best-candidate queries.
/// </summary>
public sealed class FileSystemHarnessCandidateRepository : IHarnessCandidateRepository, IDisposable
{
    private readonly IOptionsMonitor<MetaHarnessConfig> _options;
    private readonly ConcurrentDictionary<Guid, IndexLock> _indexLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Initializes a new instance of <see cref="FileSystemHarnessCandidateRepository"/>.
    /// </summary>
    public FileSystemHarnessCandidateRepository(IOptionsMonitor<MetaHarnessConfig> options)
    {
        _options = options;
    }

    /// <summary>
    /// The number of optimization runs currently holding or awaiting the index lock. Exposed so
    /// the eviction tests can observe it; not part of <see cref="IHarnessCandidateRepository"/>.
    /// </summary>
    internal int TrackedIndexLocks => _indexLocks.Count;

    /// <inheritdoc/>
    public async Task SaveAsync(HarnessCandidate candidate, CancellationToken ct = default)
    {
        var dir = CandidateDir(candidate.OptimizationRunId, candidate.CandidateId);
        Directory.CreateDirectory(dir);

        var dto = new CandidateFileContent { Candidate = candidate, WriteCompleted = true };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await WriteAtomicAsync(Path.Combine(dir, "candidate.json"), json, ct);

        var entry = Reserve(candidate.OptimizationRunId);

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            // Cancelled while queued. The reservation has to come back off, or an abandoned wait
            // pins the entry for the lifetime of the host — the same leak by a different route.
            Unreserve(candidate.OptimizationRunId, entry);
            throw;
        }

        try
        {
            var indexPath = IndexPath(candidate.OptimizationRunId);
            var record = new IndexRecord
            {
                CandidateId = candidate.CandidateId,
                PassRate = candidate.BestScore,
                TokenCost = candidate.TokenCost,
                Status = candidate.Status,
                Iteration = candidate.Iteration
            };

            var existing = File.Exists(indexPath)
                ? await File.ReadAllLinesAsync(indexPath, ct)
                : Array.Empty<string>();

            var newLine = JsonSerializer.Serialize(record, IndexOptions);
            var tmp = indexPath + ".tmp";
            await File.WriteAllLinesAsync(tmp, existing.Append(newLine), ct);
            File.Move(tmp, indexPath, overwrite: true);
        }
        finally
        {
            // Release before unreserving. Unreserving can dispose the semaphore, and disposing one
            // that has not been released loses the slot for every future acquirer of a re-created
            // entry — which is a deadlock, not a leak.
            entry.Semaphore.Release();
            Unreserve(candidate.OptimizationRunId, entry);
        }
    }

    /// <inheritdoc/>
    public async Task<HarnessCandidate?> GetAsync(Guid candidateId, CancellationToken ct = default)
    {
        var root = _options.CurrentValue.TraceDirectoryRoot;
        var optsDir = Path.Combine(root, "optimizations");

        if (!Directory.Exists(optsDir))
            return null;

        foreach (var runDir in Directory.EnumerateDirectories(optsDir))
        {
            var path = Path.Combine(runDir, "candidates", candidateId.ToString("D"), "candidate.json");
            var candidate = await TryReadCandidateAsync(path, ct);
            if (candidate is not null)
                return candidate;
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HarnessCandidate>> GetLineageAsync(Guid candidateId, CancellationToken ct = default)
    {
        var start = await GetAsync(candidateId, ct);
        if (start is null)
            return [];

        var chain = new List<HarnessCandidate>();
        var current = start;

        while (current is not null)
        {
            chain.Add(current);
            if (current.ParentCandidateId is null)
                break;
            current = await GetWithinRunAsync(current.ParentCandidateId.Value, current.OptimizationRunId, ct);
        }

        chain.Reverse();
        return chain;
    }

    /// <inheritdoc/>
    public async Task<HarnessCandidate?> GetBestAsync(Guid optimizationRunId, CancellationToken ct = default)
    {
        var indexPath = IndexPath(optimizationRunId);
        if (!File.Exists(indexPath))
            return null;

        var lines = await File.ReadAllLinesAsync(indexPath, ct);
        var latest = new Dictionary<Guid, IndexRecord>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var record = JsonSerializer.Deserialize<IndexRecord>(line, IndexOptions);
                if (record is not null)
                    latest[record.CandidateId] = record;
            }
            catch (JsonException) { /* skip corrupt index lines */ }
        }

        var winner = latest.Values
            .Where(r => r.Status == HarnessCandidateStatus.Evaluated)
            .OrderByDescending(r => r.PassRate ?? 0.0)
            .ThenBy(r => r.TokenCost ?? long.MaxValue)
            .ThenBy(r => r.Iteration)
            .FirstOrDefault();

        if (winner is null)
            return null;

        return await GetWithinRunAsync(winner.CandidateId, optimizationRunId, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HarnessCandidate>> ListAsync(Guid optimizationRunId, CancellationToken ct = default)
    {
        var indexPath = IndexPath(optimizationRunId);
        if (!File.Exists(indexPath))
            return [];

        var lines = await File.ReadAllLinesAsync(indexPath, ct);
        var seen = new HashSet<Guid>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var record = JsonSerializer.Deserialize<IndexRecord>(line, IndexOptions);
                if (record is not null)
                    seen.Add(record.CandidateId);
            }
            catch (JsonException) { /* skip corrupt index lines */ }
        }

        var results = new List<HarnessCandidate>();
        foreach (var id in seen)
        {
            var candidate = await GetWithinRunAsync(id, optimizationRunId, ct);
            if (candidate is not null)
                results.Add(candidate);
        }

        return results;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Entries evict themselves as soon as nothing holds or awaits them, so in a settled host
    /// this has nothing to do. It remains as the backstop for disposal mid-save.
    /// </remarks>
    public void Dispose()
    {
        foreach (var entry in _indexLocks.Values)
        {
            // Take the entry's own gate and mark it evicted rather than disposing blind. Disposal
            // racing an in-flight Unreserve would otherwise dispose the same semaphore twice, and
            // a reserver that has already published a successor entry would be handed a disposed
            // one. Marking under the gate makes both paths agree on who disposes.
            lock (entry.Gate)
            {
                if (entry.Evicted)
                    continue;

                entry.Evicted = true;
                entry.Semaphore.Dispose();
            }
        }

        _indexLocks.Clear();
    }

    // -------------------------------------------------------------------------
    // Index-lock lifetime
    // -------------------------------------------------------------------------

    /// <summary>
    /// Takes a reference on the run's index lock, creating it if needed.
    /// </summary>
    /// <remarks>
    /// The retry loop closes the window between <c>GetOrAdd</c> handing back an entry and this
    /// thread taking its reference: a releasing thread may evict that entry in between. Eviction
    /// sets <see cref="IndexLock.Evicted"/> under the entry's own lock, so a reserver that loses
    /// the race sees the flag and starts again against whatever is in the dictionary now — rather
    /// than waiting on a semaphore no future writer will look up.
    /// <para>
    /// What makes the retry terminate is that eviction also removes the entry from the dictionary
    /// under that same lock, so the next <c>GetOrAdd</c> cannot hand back the flagged one.
    /// Flagging without removing turns this into a spin that never ends.
    /// </para>
    /// </remarks>
    private IndexLock Reserve(Guid optimizationRunId)
    {
        while (true)
        {
            var entry = _indexLocks.GetOrAdd(optimizationRunId, static _ => new IndexLock());

            lock (entry.Gate)
            {
                if (!entry.Evicted)
                {
                    entry.References++;
                    return entry;
                }
            }
        }
    }

    /// <summary>
    /// Drops a reference and evicts the entry once nothing holds or awaits it.
    /// </summary>
    /// <remarks>
    /// Disposing the semaphore here is safe precisely because the count reached zero: no thread
    /// holds it and none is waiting on it, and a thread about to wait is still blocked on
    /// <see cref="IndexLock.Gate"/> in <see cref="Reserve"/> and will see the eviction flag
    /// instead. The key-and-value overload of <c>TryRemove</c> matters — removing by key alone
    /// could delete a successor entry a concurrent reserver has already published.
    /// </remarks>
    private void Unreserve(Guid optimizationRunId, IndexLock entry)
    {
        lock (entry.Gate)
        {
            if (--entry.References > 0)
                return;

            // Already evicted means Dispose got here first and owns the semaphore.
            if (entry.Evicted)
                return;

            entry.Evicted = true;
            _indexLocks.TryRemove(new KeyValuePair<Guid, IndexLock>(optimizationRunId, entry));
            entry.Semaphore.Dispose();
        }
    }

    /// <summary>
    /// Per-run index-file lock plus the reference count that decides when it is dropped.
    /// </summary>
    private sealed class IndexLock
    {
        /// <summary>Guards <see cref="References"/> and <see cref="Evicted"/>. Never held across an await.</summary>
        public object Gate { get; } = new();

        /// <summary>The binary lock serialising index writes for one optimization run.</summary>
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Holders plus waiters. Guarded by <see cref="Gate"/>.</summary>
        public int References { get; set; }

        /// <summary>Set once this entry has left the dictionary, so a racing reserver retries.</summary>
        public bool Evicted { get; set; }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private string CandidatesRoot(Guid optimizationRunId) =>
        Path.Combine(_options.CurrentValue.TraceDirectoryRoot, "optimizations", optimizationRunId.ToString("D"), "candidates");

    private string CandidateDir(Guid optimizationRunId, Guid candidateId) =>
        Path.Combine(CandidatesRoot(optimizationRunId), candidateId.ToString("D"));

    private string IndexPath(Guid optimizationRunId) =>
        Path.Combine(CandidatesRoot(optimizationRunId), "index.jsonl");

    private async Task<HarnessCandidate?> GetWithinRunAsync(Guid candidateId, Guid optimizationRunId, CancellationToken ct)
    {
        var path = Path.Combine(CandidateDir(optimizationRunId, candidateId), "candidate.json");
        return await TryReadCandidateAsync(path, ct);
    }

    private static async Task<HarnessCandidate?> TryReadCandidateAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var dto = JsonSerializer.Deserialize<CandidateFileContent>(json, JsonOptions);
            return dto is { WriteCompleted: true } ? dto.Candidate : null;
        }
        catch (JsonException)
        {
            return null; // treat corrupt file as not found
        }
    }

    private static async Task WriteAtomicAsync(string targetPath, string content, CancellationToken ct)
    {
        var tmp = targetPath + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, targetPath, overwrite: true);
    }

    // -------------------------------------------------------------------------
    // Private DTOs
    // -------------------------------------------------------------------------

    private sealed class CandidateFileContent
    {
        public HarnessCandidate? Candidate { get; init; }
        public bool WriteCompleted { get; init; }
    }

    private sealed class IndexRecord
    {
        public Guid CandidateId { get; init; }
        public double? PassRate { get; init; }
        public long? TokenCost { get; init; }
        public HarnessCandidateStatus Status { get; init; }
        public int Iteration { get; init; }
    }
}

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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _indexLocks = new();

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

    /// <inheritdoc/>
    public async Task SaveAsync(HarnessCandidate candidate, CancellationToken ct = default)
    {
        var dir = CandidateDir(candidate.OptimizationRunId, candidate.CandidateId);
        Directory.CreateDirectory(dir);

        var dto = new CandidateFileContent { Candidate = candidate, WriteCompleted = true };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await WriteAtomicAsync(Path.Combine(dir, "candidate.json"), json, ct);

        var semaphore = _indexLocks.GetOrAdd(candidate.OptimizationRunId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
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
            semaphore.Release();
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
    public void Dispose()
    {
        foreach (var semaphore in _indexLocks.Values)
            semaphore.Dispose();
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

diff --git a/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessCandidateRepository.cs b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessCandidateRepository.cs
new file mode 100644
index 0000000..d598ad1
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessCandidateRepository.cs
@@ -0,0 +1,33 @@
+using Domain.Common.MetaHarness;
+
+namespace Application.AI.Common.Interfaces.MetaHarness;
+
+/// <summary>
+/// Persistence store for <see cref="HarnessCandidate"/> records.
+/// All write operations must be atomic (temp-file + rename).
+/// </summary>
+public interface IHarnessCandidateRepository
+{
+    /// <summary>Persists a candidate and updates the run index.</summary>
+    Task SaveAsync(HarnessCandidate candidate, CancellationToken ct = default);
+
+    /// <summary>Returns the candidate with the given ID, or null if not found.</summary>
+    Task<HarnessCandidate?> GetAsync(Guid candidateId, CancellationToken ct = default);
+
+    /// <summary>
+    /// Returns the full ancestor chain ending at <paramref name="candidateId"/>,
+    /// ordered oldest-first (seed candidate at index 0).
+    /// </summary>
+    Task<IReadOnlyList<HarnessCandidate>> GetLineageAsync(Guid candidateId, CancellationToken ct = default);
+
+    /// <summary>
+    /// Returns the best evaluated candidate for the given run using tie-breaking:
+    /// (1) highest pass rate, (2) lowest token cost, (3) lowest iteration.
+    /// Reads only the index file to select the winner — does not open candidate.json
+    /// files for non-winning candidates.
+    /// </summary>
+    Task<HarnessCandidate?> GetBestAsync(Guid optimizationRunId, CancellationToken ct = default);
+
+    /// <summary>Returns all candidates for the given optimization run.</summary>
+    Task<IReadOnlyList<HarnessCandidate>> ListAsync(Guid optimizationRunId, CancellationToken ct = default);
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index 51c17c2..e868602 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -79,6 +79,9 @@ public static class DependencyInjection
         // Snapshot builder — captures live harness config into a redacted, hashed snapshot
         services.AddSingleton<ISnapshotBuilder, ActiveConfigSnapshotBuilder>();
 
+        // Candidate repository — filesystem-backed persistence with atomic writes and JSONL index
+        services.AddSingleton<IHarnessCandidateRepository, FileSystemHarnessCandidateRepository>();
+
         // Execution trace store — filesystem-backed per-run trace artifact persistence
         services.AddSingleton<IExecutionTraceStore, FileSystemExecutionTraceStore>();
 
diff --git a/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/FileSystemHarnessCandidateRepository.cs b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/FileSystemHarnessCandidateRepository.cs
new file mode 100644
index 0000000..e361194
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/FileSystemHarnessCandidateRepository.cs
@@ -0,0 +1,239 @@
+using System.Collections.Concurrent;
+using System.Text.Json;
+using System.Text.Json.Serialization;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.MetaHarness;
+
+/// <summary>
+/// Filesystem-backed implementation of <see cref="IHarnessCandidateRepository"/>.
+/// Stores each candidate as an atomic JSON file under
+/// <c>{TraceDirectoryRoot}/optimizations/{runId}/candidates/{candidateId}/candidate.json</c>
+/// and maintains a lightweight <c>index.jsonl</c> per run for O(n) best-candidate queries.
+/// </summary>
+public sealed class FileSystemHarnessCandidateRepository : IHarnessCandidateRepository
+{
+    private readonly IOptionsMonitor<MetaHarnessConfig> _options;
+    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _indexLocks = new();
+
+    private static readonly JsonSerializerOptions JsonOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
+        WriteIndented = true,
+        Converters = { new JsonStringEnumConverter() }
+    };
+
+    private static readonly JsonSerializerOptions IndexOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
+        Converters = { new JsonStringEnumConverter() }
+    };
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="FileSystemHarnessCandidateRepository"/>.
+    /// </summary>
+    public FileSystemHarnessCandidateRepository(IOptionsMonitor<MetaHarnessConfig> options)
+    {
+        _options = options;
+    }
+
+    /// <inheritdoc/>
+    public async Task SaveAsync(HarnessCandidate candidate, CancellationToken ct = default)
+    {
+        var dir = CandidateDir(candidate.OptimizationRunId, candidate.CandidateId);
+        Directory.CreateDirectory(dir);
+
+        var dto = new CandidateFileContent { Candidate = candidate, WriteCompleted = true };
+        var json = JsonSerializer.Serialize(dto, JsonOptions);
+        await WriteAtomicAsync(Path.Combine(dir, "candidate.json"), json, ct);
+
+        var semaphore = _indexLocks.GetOrAdd(candidate.OptimizationRunId, _ => new SemaphoreSlim(1, 1));
+        await semaphore.WaitAsync(ct);
+        try
+        {
+            var indexPath = IndexPath(candidate.OptimizationRunId);
+            var record = new IndexRecord
+            {
+                CandidateId = candidate.CandidateId,
+                PassRate = candidate.BestScore,
+                TokenCost = candidate.TokenCost,
+                Status = candidate.Status,
+                Iteration = candidate.Iteration
+            };
+
+            var existing = File.Exists(indexPath)
+                ? await File.ReadAllLinesAsync(indexPath, ct)
+                : Array.Empty<string>();
+
+            var newLine = JsonSerializer.Serialize(record, IndexOptions);
+            var tmp = indexPath + ".tmp";
+            await File.WriteAllLinesAsync(tmp, existing.Append(newLine), ct);
+            File.Move(tmp, indexPath, overwrite: true);
+        }
+        finally
+        {
+            semaphore.Release();
+        }
+    }
+
+    /// <inheritdoc/>
+    public async Task<HarnessCandidate?> GetAsync(Guid candidateId, CancellationToken ct = default)
+    {
+        var root = _options.CurrentValue.TraceDirectoryRoot;
+        var optsDir = Path.Combine(root, "optimizations");
+
+        if (!Directory.Exists(optsDir))
+            return null;
+
+        foreach (var runDir in Directory.EnumerateDirectories(optsDir))
+        {
+            var path = Path.Combine(runDir, "candidates", candidateId.ToString("D"), "candidate.json");
+            var candidate = await TryReadCandidateAsync(path, ct);
+            if (candidate is not null)
+                return candidate;
+        }
+
+        return null;
+    }
+
+    /// <inheritdoc/>
+    public async Task<IReadOnlyList<HarnessCandidate>> GetLineageAsync(Guid candidateId, CancellationToken ct = default)
+    {
+        var start = await GetAsync(candidateId, ct);
+        if (start is null)
+            return [];
+
+        var chain = new List<HarnessCandidate>();
+        var current = start;
+
+        while (current is not null)
+        {
+            chain.Insert(0, current);
+            if (current.ParentCandidateId is null)
+                break;
+            current = await GetWithinRunAsync(current.ParentCandidateId.Value, current.OptimizationRunId, ct);
+        }
+
+        return chain;
+    }
+
+    /// <inheritdoc/>
+    public async Task<HarnessCandidate?> GetBestAsync(Guid optimizationRunId, CancellationToken ct = default)
+    {
+        var indexPath = IndexPath(optimizationRunId);
+        if (!File.Exists(indexPath))
+            return null;
+
+        var lines = await File.ReadAllLinesAsync(indexPath, ct);
+        var latest = new Dictionary<Guid, IndexRecord>();
+
+        foreach (var line in lines)
+        {
+            if (string.IsNullOrWhiteSpace(line))
+                continue;
+            var record = JsonSerializer.Deserialize<IndexRecord>(line, IndexOptions);
+            if (record is not null)
+                latest[record.CandidateId] = record;
+        }
+
+        var winner = latest.Values
+            .Where(r => r.Status == HarnessCandidateStatus.Evaluated)
+            .OrderByDescending(r => r.PassRate ?? 0.0)
+            .ThenBy(r => r.TokenCost ?? long.MaxValue)
+            .ThenBy(r => r.Iteration)
+            .FirstOrDefault();
+
+        if (winner is null)
+            return null;
+
+        return await GetWithinRunAsync(winner.CandidateId, optimizationRunId, ct);
+    }
+
+    /// <inheritdoc/>
+    public async Task<IReadOnlyList<HarnessCandidate>> ListAsync(Guid optimizationRunId, CancellationToken ct = default)
+    {
+        var indexPath = IndexPath(optimizationRunId);
+        if (!File.Exists(indexPath))
+            return [];
+
+        var lines = await File.ReadAllLinesAsync(indexPath, ct);
+        var seen = new HashSet<Guid>();
+
+        foreach (var line in lines)
+        {
+            if (string.IsNullOrWhiteSpace(line))
+                continue;
+            var record = JsonSerializer.Deserialize<IndexRecord>(line, IndexOptions);
+            if (record is not null)
+                seen.Add(record.CandidateId);
+        }
+
+        var results = new List<HarnessCandidate>();
+        foreach (var id in seen)
+        {
+            var candidate = await GetWithinRunAsync(id, optimizationRunId, ct);
+            if (candidate is not null)
+                results.Add(candidate);
+        }
+
+        return results;
+    }
+
+    // -------------------------------------------------------------------------
+    // Private helpers
+    // -------------------------------------------------------------------------
+
+    private string CandidatesRoot(Guid optimizationRunId) =>
+        Path.Combine(_options.CurrentValue.TraceDirectoryRoot, "optimizations", optimizationRunId.ToString("D"), "candidates");
+
+    private string CandidateDir(Guid optimizationRunId, Guid candidateId) =>
+        Path.Combine(CandidatesRoot(optimizationRunId), candidateId.ToString("D"));
+
+    private string IndexPath(Guid optimizationRunId) =>
+        Path.Combine(CandidatesRoot(optimizationRunId), "index.jsonl");
+
+    private async Task<HarnessCandidate?> GetWithinRunAsync(Guid candidateId, Guid optimizationRunId, CancellationToken ct)
+    {
+        var path = Path.Combine(CandidateDir(optimizationRunId, candidateId), "candidate.json");
+        return await TryReadCandidateAsync(path, ct);
+    }
+
+    private static async Task<HarnessCandidate?> TryReadCandidateAsync(string path, CancellationToken ct)
+    {
+        if (!File.Exists(path))
+            return null;
+
+        var json = await File.ReadAllTextAsync(path, ct);
+        var dto = JsonSerializer.Deserialize<CandidateFileContent>(json, JsonOptions);
+        return dto is { WriteCompleted: true } ? dto.Candidate : null;
+    }
+
+    private static async Task WriteAtomicAsync(string targetPath, string content, CancellationToken ct)
+    {
+        var tmp = targetPath + ".tmp";
+        await File.WriteAllTextAsync(tmp, content, ct);
+        File.Move(tmp, targetPath, overwrite: true);
+    }
+
+    // -------------------------------------------------------------------------
+    // Private DTOs
+    // -------------------------------------------------------------------------
+
+    private sealed class CandidateFileContent
+    {
+        public HarnessCandidate? Candidate { get; init; }
+        public bool WriteCompleted { get; init; }
+    }
+
+    private sealed class IndexRecord
+    {
+        public Guid CandidateId { get; init; }
+        public double? PassRate { get; init; }
+        public long? TokenCost { get; init; }
+        public HarnessCandidateStatus Status { get; init; }
+        public int Iteration { get; init; }
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs
new file mode 100644
index 0000000..3f7b93f
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs
@@ -0,0 +1,262 @@
+using Application.AI.Common.Interfaces.MetaHarness;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Infrastructure.AI.MetaHarness;
+using Microsoft.Extensions.Options;
+using Moq;
+using System.Text.Json;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.MetaHarness;
+
+/// <summary>
+/// Tests for FileSystemHarnessCandidateRepository: round-trip persistence,
+/// lineage chains, index integrity, and best-candidate selection.
+/// </summary>
+public class FileSystemHarnessCandidateRepositoryTests : IDisposable
+{
+    private readonly string _root;
+    private readonly IHarnessCandidateRepository _sut;
+
+    public FileSystemHarnessCandidateRepositoryTests()
+    {
+        _root = Path.Combine(Path.GetTempPath(), $"repo-tests-{Guid.NewGuid():N}");
+        Directory.CreateDirectory(_root);
+
+        var config = new MetaHarnessConfig { TraceDirectoryRoot = _root };
+        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
+        _sut = new FileSystemHarnessCandidateRepository(opts);
+    }
+
+    public void Dispose() => Directory.Delete(_root, recursive: true);
+
+    // -------------------------------------------------------------------------
+    // Round-trip and basic persistence
+    // -------------------------------------------------------------------------
+
+    [Fact]
+    public async Task SaveAsync_CreatesExpectedDirectoryAndCandidateJson()
+    {
+        var candidate = BuildProposed(Guid.NewGuid());
+        await _sut.SaveAsync(candidate);
+
+        var expectedPath = Path.Combine(
+            _root, "optimizations", candidate.OptimizationRunId.ToString("D"),
+            "candidates", candidate.CandidateId.ToString("D"), "candidate.json");
+
+        Assert.True(File.Exists(expectedPath));
+    }
+
+    [Fact]
+    public async Task SaveAsync_WritesAtomically_CandidateJsonHasWriteCompletedTrue()
+    {
+        var candidate = BuildProposed(Guid.NewGuid());
+        await _sut.SaveAsync(candidate);
+
+        var path = CandidateJsonPath(candidate);
+        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
+        Assert.True(doc.RootElement.GetProperty("writeCompleted").GetBoolean());
+    }
+
+    [Fact]
+    public async Task GetAsync_ReturnsCandidate_AfterSave()
+    {
+        var candidate = BuildProposed(Guid.NewGuid());
+        await _sut.SaveAsync(candidate);
+
+        var result = await _sut.GetAsync(candidate.CandidateId);
+
+        Assert.NotNull(result);
+        Assert.Equal(candidate.CandidateId, result.CandidateId);
+        Assert.Equal(candidate.OptimizationRunId, result.OptimizationRunId);
+        Assert.Equal(candidate.Status, result.Status);
+        Assert.Equal(candidate.Iteration, result.Iteration);
+    }
+
+    [Fact]
+    public async Task GetAsync_NonExistentCandidateId_ReturnsNull()
+    {
+        var result = await _sut.GetAsync(Guid.NewGuid());
+        Assert.Null(result);
+    }
+
+    // -------------------------------------------------------------------------
+    // Lineage chain
+    // -------------------------------------------------------------------------
+
+    [Fact]
+    public async Task GetLineageAsync_NoParent_ReturnsSingleElement()
+    {
+        var seed = BuildProposed(Guid.NewGuid());
+        await _sut.SaveAsync(seed);
+
+        var lineage = await _sut.GetLineageAsync(seed.CandidateId);
+
+        Assert.Single(lineage);
+        Assert.Equal(seed.CandidateId, lineage[0].CandidateId);
+    }
+
+    [Fact]
+    public async Task GetLineageAsync_ThreeGenerations_ReturnsChainOldestFirst()
+    {
+        var runId = Guid.NewGuid();
+        var grandparent = BuildProposed(runId);
+        var parent = BuildProposed(runId, parentId: grandparent.CandidateId);
+        var child = BuildProposed(runId, parentId: parent.CandidateId);
+
+        await _sut.SaveAsync(grandparent);
+        await _sut.SaveAsync(parent);
+        await _sut.SaveAsync(child);
+
+        var lineage = await _sut.GetLineageAsync(child.CandidateId);
+
+        Assert.Equal(3, lineage.Count);
+        Assert.Equal(grandparent.CandidateId, lineage[0].CandidateId);
+        Assert.Equal(parent.CandidateId, lineage[1].CandidateId);
+        Assert.Equal(child.CandidateId, lineage[2].CandidateId);
+    }
+
+    // -------------------------------------------------------------------------
+    // Index and best-candidate selection
+    // -------------------------------------------------------------------------
+
+    [Fact]
+    public async Task SaveAsync_UpdatesIndexJsonl_Atomically()
+    {
+        var candidate = BuildProposed(Guid.NewGuid());
+        await _sut.SaveAsync(candidate);
+
+        var indexPath = IndexPath(candidate.OptimizationRunId);
+        Assert.True(File.Exists(indexPath));
+
+        var lines = await File.ReadAllLinesAsync(indexPath);
+        Assert.All(lines.Where(l => !string.IsNullOrWhiteSpace(l)), line =>
+        {
+            using var doc = JsonDocument.Parse(line);
+            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
+        });
+    }
+
+    [Fact]
+    public async Task ListAsync_ReturnsAllCandidatesForRun()
+    {
+        var runId = Guid.NewGuid();
+        var c1 = BuildProposed(runId);
+        var c2 = BuildProposed(runId);
+        var c3 = BuildProposed(runId);
+
+        await _sut.SaveAsync(c1);
+        await _sut.SaveAsync(c2);
+        await _sut.SaveAsync(c3);
+
+        var list = await _sut.ListAsync(runId);
+        Assert.Equal(3, list.Count);
+    }
+
+    [Fact]
+    public async Task GetBestAsync_ReadsIndexOnly_NotCandidateFiles()
+    {
+        var runId = Guid.NewGuid();
+        var c1 = BuildEvaluated(runId, passRate: 0.5, iteration: 0);
+        var c2 = BuildEvaluated(runId, passRate: 0.8, iteration: 1); // winner
+        var c3 = BuildEvaluated(runId, passRate: 0.6, iteration: 2);
+
+        await _sut.SaveAsync(c1);
+        await _sut.SaveAsync(c2);
+        await _sut.SaveAsync(c3);
+
+        // Delete non-winner candidate files — selection must use index only
+        File.Delete(CandidateJsonPath(c1));
+        File.Delete(CandidateJsonPath(c3));
+
+        var best = await _sut.GetBestAsync(runId);
+
+        Assert.NotNull(best);
+        Assert.Equal(c2.CandidateId, best.CandidateId);
+    }
+
+    [Fact]
+    public async Task GetBestAsync_MultipleEvaluatedCandidates_ReturnsHighestPassRate()
+    {
+        var runId = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.5, iteration: 0));
+        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.9, iteration: 1));
+        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.7, iteration: 2));
+
+        var best = await _sut.GetBestAsync(runId);
+
+        Assert.NotNull(best);
+        Assert.Equal(0.9, best.BestScore);
+    }
+
+    [Fact]
+    public async Task GetBestAsync_TieOnPassRate_ReturnsLowerTokenCost()
+    {
+        var runId = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 1000, iteration: 0));
+        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 500, iteration: 1));
+
+        var best = await _sut.GetBestAsync(runId);
+
+        Assert.NotNull(best);
+        Assert.Equal(500, best.TokenCost);
+    }
+
+    [Fact]
+    public async Task GetBestAsync_TieOnBoth_ReturnsEarlierIteration()
+    {
+        var runId = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 500, iteration: 3));
+        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 500, iteration: 1));
+
+        var best = await _sut.GetBestAsync(runId);
+
+        Assert.NotNull(best);
+        Assert.Equal(1, best.Iteration);
+    }
+
+    // -------------------------------------------------------------------------
+    // Helpers
+    // -------------------------------------------------------------------------
+
+    private static HarnessSnapshot EmptySnapshot() => new()
+    {
+        SkillFileSnapshots = new Dictionary<string, string>(),
+        SystemPromptSnapshot = string.Empty,
+        ConfigSnapshot = new Dictionary<string, string>(),
+        SnapshotManifest = []
+    };
+
+    private static HarnessCandidate BuildProposed(Guid runId, Guid? parentId = null, int iteration = 0) =>
+        new()
+        {
+            CandidateId = Guid.NewGuid(),
+            OptimizationRunId = runId,
+            ParentCandidateId = parentId,
+            Iteration = iteration,
+            CreatedAt = DateTimeOffset.UtcNow,
+            Snapshot = EmptySnapshot(),
+            Status = HarnessCandidateStatus.Proposed
+        };
+
+    private static HarnessCandidate BuildEvaluated(
+        Guid runId, double passRate = 0.5, long tokenCost = 100, int iteration = 0) =>
+        new()
+        {
+            CandidateId = Guid.NewGuid(),
+            OptimizationRunId = runId,
+            Iteration = iteration,
+            CreatedAt = DateTimeOffset.UtcNow,
+            Snapshot = EmptySnapshot(),
+            Status = HarnessCandidateStatus.Evaluated,
+            BestScore = passRate,
+            TokenCost = tokenCost
+        };
+
+    private string CandidateJsonPath(HarnessCandidate c) =>
+        Path.Combine(_root, "optimizations", c.OptimizationRunId.ToString("D"),
+            "candidates", c.CandidateId.ToString("D"), "candidate.json");
+
+    private string IndexPath(Guid runId) =>
+        Path.Combine(_root, "optimizations", runId.ToString("D"), "candidates", "index.jsonl");
+}

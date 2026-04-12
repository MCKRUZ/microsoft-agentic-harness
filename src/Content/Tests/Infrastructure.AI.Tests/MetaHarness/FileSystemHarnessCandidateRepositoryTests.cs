using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Tests for FileSystemHarnessCandidateRepository: round-trip persistence,
/// lineage chains, index integrity, and best-candidate selection.
/// </summary>
public class FileSystemHarnessCandidateRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly IHarnessCandidateRepository _sut;

    public FileSystemHarnessCandidateRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"repo-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var config = new MetaHarnessConfig { TraceDirectoryRoot = _root };
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
        _sut = new FileSystemHarnessCandidateRepository(opts);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // -------------------------------------------------------------------------
    // Round-trip and basic persistence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_CreatesExpectedDirectoryAndCandidateJson()
    {
        var candidate = BuildProposed(Guid.NewGuid());
        await _sut.SaveAsync(candidate);

        var expectedPath = Path.Combine(
            _root, "optimizations", candidate.OptimizationRunId.ToString("D"),
            "candidates", candidate.CandidateId.ToString("D"), "candidate.json");

        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task SaveAsync_WritesAtomically_CandidateJsonHasWriteCompletedTrue()
    {
        var candidate = BuildProposed(Guid.NewGuid());
        await _sut.SaveAsync(candidate);

        var path = CandidateJsonPath(candidate);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.True(doc.RootElement.GetProperty("writeCompleted").GetBoolean());
    }

    [Fact]
    public async Task GetAsync_ReturnsCandidate_AfterSave()
    {
        var candidate = BuildProposed(Guid.NewGuid());
        await _sut.SaveAsync(candidate);

        var result = await _sut.GetAsync(candidate.CandidateId);

        Assert.NotNull(result);
        Assert.Equal(candidate.CandidateId, result.CandidateId);
        Assert.Equal(candidate.OptimizationRunId, result.OptimizationRunId);
        Assert.Equal(candidate.Status, result.Status);
        Assert.Equal(candidate.Iteration, result.Iteration);
    }

    [Fact]
    public async Task GetAsync_NonExistentCandidateId_ReturnsNull()
    {
        var result = await _sut.GetAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // Lineage chain
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetLineageAsync_NoParent_ReturnsSingleElement()
    {
        var seed = BuildProposed(Guid.NewGuid());
        await _sut.SaveAsync(seed);

        var lineage = await _sut.GetLineageAsync(seed.CandidateId);

        Assert.Single(lineage);
        Assert.Equal(seed.CandidateId, lineage[0].CandidateId);
    }

    [Fact]
    public async Task GetLineageAsync_ThreeGenerations_ReturnsChainOldestFirst()
    {
        var runId = Guid.NewGuid();
        var grandparent = BuildProposed(runId);
        var parent = BuildProposed(runId, parentId: grandparent.CandidateId);
        var child = BuildProposed(runId, parentId: parent.CandidateId);

        await _sut.SaveAsync(grandparent);
        await _sut.SaveAsync(parent);
        await _sut.SaveAsync(child);

        var lineage = await _sut.GetLineageAsync(child.CandidateId);

        Assert.Equal(3, lineage.Count);
        Assert.Equal(grandparent.CandidateId, lineage[0].CandidateId);
        Assert.Equal(parent.CandidateId, lineage[1].CandidateId);
        Assert.Equal(child.CandidateId, lineage[2].CandidateId);
    }

    // -------------------------------------------------------------------------
    // Index and best-candidate selection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_UpdatesIndexJsonl_Atomically()
    {
        var candidate = BuildProposed(Guid.NewGuid());
        await _sut.SaveAsync(candidate);

        var indexPath = IndexPath(candidate.OptimizationRunId);
        Assert.True(File.Exists(indexPath));

        var lines = await File.ReadAllLinesAsync(indexPath);
        Assert.All(lines.Where(l => !string.IsNullOrWhiteSpace(l)), line =>
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        });
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCandidatesForRun()
    {
        var runId = Guid.NewGuid();
        var c1 = BuildProposed(runId);
        var c2 = BuildProposed(runId);
        var c3 = BuildProposed(runId);

        await _sut.SaveAsync(c1);
        await _sut.SaveAsync(c2);
        await _sut.SaveAsync(c3);

        var list = await _sut.ListAsync(runId);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task GetBestAsync_ReadsIndexOnly_NotCandidateFiles()
    {
        var runId = Guid.NewGuid();
        var c1 = BuildEvaluated(runId, passRate: 0.5, iteration: 0);
        var c2 = BuildEvaluated(runId, passRate: 0.8, iteration: 1); // winner
        var c3 = BuildEvaluated(runId, passRate: 0.6, iteration: 2);

        await _sut.SaveAsync(c1);
        await _sut.SaveAsync(c2);
        await _sut.SaveAsync(c3);

        // Delete non-winner candidate files — selection must use index only
        File.Delete(CandidateJsonPath(c1));
        File.Delete(CandidateJsonPath(c3));

        var best = await _sut.GetBestAsync(runId);

        Assert.NotNull(best);
        Assert.Equal(c2.CandidateId, best.CandidateId);
    }

    [Fact]
    public async Task GetBestAsync_MultipleEvaluatedCandidates_ReturnsHighestPassRate()
    {
        var runId = Guid.NewGuid();
        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.5, iteration: 0));
        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.9, iteration: 1));
        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.7, iteration: 2));

        var best = await _sut.GetBestAsync(runId);

        Assert.NotNull(best);
        Assert.Equal(0.9, best.BestScore);
    }

    [Fact]
    public async Task GetBestAsync_TieOnPassRate_ReturnsLowerTokenCost()
    {
        var runId = Guid.NewGuid();
        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 1000, iteration: 0));
        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 500, iteration: 1));

        var best = await _sut.GetBestAsync(runId);

        Assert.NotNull(best);
        Assert.Equal(500, best.TokenCost);
    }

    [Fact]
    public async Task GetBestAsync_TieOnBoth_ReturnsEarlierIteration()
    {
        var runId = Guid.NewGuid();
        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 500, iteration: 3));
        await _sut.SaveAsync(BuildEvaluated(runId, passRate: 0.8, tokenCost: 500, iteration: 1));

        var best = await _sut.GetBestAsync(runId);

        Assert.NotNull(best);
        Assert.Equal(1, best.Iteration);
    }

    [Fact]
    public async Task SaveAsync_SaveTwiceSameCandidate_IndexUsesLastLineWins()
    {
        var runId = Guid.NewGuid();
        var candidate = BuildProposed(runId);
        await _sut.SaveAsync(candidate);

        var evaluated = candidate with { Status = HarnessCandidateStatus.Evaluated, BestScore = 0.75 };
        await _sut.SaveAsync(evaluated);

        // GetBestAsync uses the index — if last-line-wins works, Evaluated candidate is returned
        var best = await _sut.GetBestAsync(runId);
        Assert.NotNull(best);
        Assert.Equal(HarnessCandidateStatus.Evaluated, best.Status);
    }

    [Fact]
    public async Task GetBestAsync_IgnoresNonEvaluatedCandidates()
    {
        var runId = Guid.NewGuid();
        await _sut.SaveAsync(BuildProposed(runId));
        var failed = BuildProposed(runId) with { Status = HarnessCandidateStatus.Failed };
        await _sut.SaveAsync(failed);

        var best = await _sut.GetBestAsync(runId);
        Assert.Null(best);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HarnessSnapshot EmptySnapshot() => new()
    {
        SkillFileSnapshots = new Dictionary<string, string>(),
        SystemPromptSnapshot = string.Empty,
        ConfigSnapshot = new Dictionary<string, string>(),
        SnapshotManifest = []
    };

    private static HarnessCandidate BuildProposed(Guid runId, Guid? parentId = null, int iteration = 0) =>
        new()
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = runId,
            ParentCandidateId = parentId,
            Iteration = iteration,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = EmptySnapshot(),
            Status = HarnessCandidateStatus.Proposed
        };

    private static HarnessCandidate BuildEvaluated(
        Guid runId, double passRate = 0.5, long tokenCost = 100, int iteration = 0) =>
        new()
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = runId,
            Iteration = iteration,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = EmptySnapshot(),
            Status = HarnessCandidateStatus.Evaluated,
            BestScore = passRate,
            TokenCost = tokenCost
        };

    private string CandidateJsonPath(HarnessCandidate c) =>
        Path.Combine(_root, "optimizations", c.OptimizationRunId.ToString("D"),
            "candidates", c.CandidateId.ToString("D"), "candidate.json");

    private string IndexPath(Guid runId) =>
        Path.Combine(_root, "optimizations", runId.ToString("D"), "candidates", "index.jsonl");
}

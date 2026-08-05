using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Issue #244: the repository kept one <see cref="SemaphoreSlim"/> per optimization run in a
/// dictionary that nothing ever removed from. Because the repository is a singleton and the key
/// is a per-run <see cref="Guid"/>, a long-lived host accumulated one entry for every run it had
/// ever executed. These tests pin the eviction and the serialisation guarantee it must not cost.
/// </summary>
public sealed class FileSystemHarnessCandidateRepositoryEvictionTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemHarnessCandidateRepository _sut;

    public FileSystemHarnessCandidateRepositoryEvictionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"repo-eviction-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var config = new MetaHarnessConfig { TraceDirectoryRoot = _root };
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
        _sut = new FileSystemHarnessCandidateRepository(opts);
    }

    public void Dispose()
    {
        _sut.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The defect itself: many sequential runs must not leave many index locks behind. Fifty runs
    /// is arbitrary but well past the point where an accumulating dictionary is unambiguous.
    /// </summary>
    [Fact]
    public async Task SaveAsync_AcrossManyRuns_LeavesNoIndexLocksBehind()
    {
        for (var i = 0; i < 50; i++)
            await _sut.SaveAsync(BuildProposed(Guid.NewGuid()));

        Assert.Equal(0, _sut.TrackedIndexLocks);
    }

    /// <summary>
    /// Repeated saves to the SAME run must also settle back to nothing, and must not leave the
    /// entry pinned by a reference that was taken but never dropped.
    /// </summary>
    [Fact]
    public async Task SaveAsync_RepeatedlyForOneRun_LeavesNoIndexLockBehind()
    {
        var runId = Guid.NewGuid();

        for (var i = 0; i < 20; i++)
            await _sut.SaveAsync(BuildProposed(runId));

        Assert.Equal(0, _sut.TrackedIndexLocks);
    }

    /// <summary>
    /// The guarantee eviction must not break. Concurrent saves to one run append to a single
    /// <c>index.jsonl</c> via read-append-replace, so losing serialisation loses index records
    /// outright — a correctness failure, not a performance one.
    /// </summary>
    [Fact]
    public async Task SaveAsync_ConcurrentSavesToSameRun_SerialiseAndKeepEveryIndexRecord()
    {
        var runId = Guid.NewGuid();
        const int saves = 40;

        await Task.WhenAll(Enumerable.Range(0, saves)
            .Select(_ => _sut.SaveAsync(BuildProposed(runId))));

        var indexPath = Path.Combine(
            _root, "optimizations", runId.ToString("D"), "candidates", "index.jsonl");

        var lines = await File.ReadAllLinesAsync(indexPath);
        Assert.Equal(saves, lines.Count(l => !string.IsNullOrWhiteSpace(l)));
        Assert.Equal(0, _sut.TrackedIndexLocks);
    }

    /// <summary>
    /// Interleaved concurrent saves across several runs: every run's records survive, and every
    /// run's lock is gone once its work finishes. This is the case where evicting under the wrong
    /// lock, or removing by key alone, would drop a successor entry another writer is using.
    /// </summary>
    [Fact]
    public async Task SaveAsync_ConcurrentSavesAcrossRuns_KeepEveryRecordAndEvictEveryLock()
    {
        var runIds = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        const int savesPerRun = 10;

        await Task.WhenAll(runIds.SelectMany(runId =>
            Enumerable.Range(0, savesPerRun).Select(_ => _sut.SaveAsync(BuildProposed(runId)))));

        foreach (var runId in runIds)
        {
            var indexPath = Path.Combine(
                _root, "optimizations", runId.ToString("D"), "candidates", "index.jsonl");
            var lines = await File.ReadAllLinesAsync(indexPath);
            Assert.Equal(savesPerRun, lines.Count(l => !string.IsNullOrWhiteSpace(l)));
        }

        Assert.Equal(0, _sut.TrackedIndexLocks);
    }

    private static HarnessSnapshot EmptySnapshot() => new()
    {
        SkillFileSnapshots = new Dictionary<string, string>(),
        SystemPromptSnapshot = string.Empty,
        ConfigSnapshot = new Dictionary<string, string>(),
        SnapshotManifest = []
    };

    private static HarnessCandidate BuildProposed(Guid runId) => new()
    {
        CandidateId = Guid.NewGuid(),
        OptimizationRunId = runId,
        Iteration = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        Snapshot = EmptySnapshot(),
        Status = HarnessCandidateStatus.Proposed
    };
}

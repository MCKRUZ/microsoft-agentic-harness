using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Integration tests for <see cref="FileSystemHarnessCandidateRepository"/> verifying
/// save, get, list, lineage, and best-candidate selection with real filesystem I/O.
/// </summary>
public sealed class FileSystemHarnessCandidateRepositoryIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemHarnessCandidateRepository _sut;
    private readonly Guid _runId = Guid.NewGuid();

    public FileSystemHarnessCandidateRepositoryIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"candidate-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new MetaHarnessConfig { TraceDirectoryRoot = _tempDir };
        var options = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(
            o => o.CurrentValue == config);

        _sut = new FileSystemHarnessCandidateRepository(options);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private HarnessCandidate CreateCandidate(
        Guid? candidateId = null,
        Guid? parentId = null,
        int iteration = 0,
        double? score = null,
        HarnessCandidateStatus status = HarnessCandidateStatus.Proposed)
    {
        return new HarnessCandidate
        {
            CandidateId = candidateId ?? Guid.NewGuid(),
            OptimizationRunId = _runId,
            ParentCandidateId = parentId,
            Iteration = iteration,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = new HarnessSnapshot
            {
                SkillFileSnapshots = new Dictionary<string, string>(),
                SystemPromptSnapshot = "test prompt",
                ConfigSnapshot = new Dictionary<string, string>(),
                SnapshotManifest = Array.Empty<SnapshotEntry>()
            },
            BestScore = score,
            Status = status
        };
    }

    [Fact]
    public async Task SaveAsync_PersistsCandidateFile()
    {
        var candidate = CreateCandidate();

        await _sut.SaveAsync(candidate);

        var loaded = await _sut.GetAsync(candidate.CandidateId);
        loaded.Should().NotBeNull();
        loaded!.CandidateId.Should().Be(candidate.CandidateId);
    }

    [Fact]
    public async Task GetAsync_NonExistent_ReturnsNull()
    {
        var result = await _sut.GetAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCandidatesForRun()
    {
        var c1 = CreateCandidate(iteration: 0);
        var c2 = CreateCandidate(iteration: 1);
        await _sut.SaveAsync(c1);
        await _sut.SaveAsync(c2);

        var list = await _sut.ListAsync(_runId);

        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAsync_EmptyRun_ReturnsEmpty()
    {
        var list = await _sut.ListAsync(Guid.NewGuid());

        list.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBestAsync_ReturnsHighestScoreEvaluatedCandidate()
    {
        var low = CreateCandidate(iteration: 0, score: 0.5, status: HarnessCandidateStatus.Evaluated);
        var high = CreateCandidate(iteration: 1, score: 0.9, status: HarnessCandidateStatus.Evaluated);
        var proposed = CreateCandidate(iteration: 2, score: 1.0, status: HarnessCandidateStatus.Proposed);

        await _sut.SaveAsync(low);
        await _sut.SaveAsync(high);
        await _sut.SaveAsync(proposed);

        var best = await _sut.GetBestAsync(_runId);

        best.Should().NotBeNull();
        best!.CandidateId.Should().Be(high.CandidateId, "only evaluated candidates should be considered");
    }

    [Fact]
    public async Task GetBestAsync_NoEvaluated_ReturnsNull()
    {
        var proposed = CreateCandidate(status: HarnessCandidateStatus.Proposed);
        await _sut.SaveAsync(proposed);

        var best = await _sut.GetBestAsync(_runId);

        best.Should().BeNull();
    }

    [Fact]
    public async Task GetBestAsync_EmptyRun_ReturnsNull()
    {
        var best = await _sut.GetBestAsync(Guid.NewGuid());

        best.Should().BeNull();
    }

    [Fact]
    public async Task GetLineageAsync_ReturnsChainFromRootToCandidate()
    {
        var root = CreateCandidate(iteration: 0);
        var child = CreateCandidate(parentId: root.CandidateId, iteration: 1);
        var grandchild = CreateCandidate(parentId: child.CandidateId, iteration: 2);

        await _sut.SaveAsync(root);
        await _sut.SaveAsync(child);
        await _sut.SaveAsync(grandchild);

        var lineage = await _sut.GetLineageAsync(grandchild.CandidateId);

        lineage.Should().HaveCount(3);
        lineage[0].CandidateId.Should().Be(root.CandidateId, "lineage starts from root");
        lineage[2].CandidateId.Should().Be(grandchild.CandidateId, "lineage ends with target");
    }

    [Fact]
    public async Task GetLineageAsync_NonExistent_ReturnsEmpty()
    {
        var lineage = await _sut.GetLineageAsync(Guid.NewGuid());

        lineage.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBestAsync_TiedScore_PrefersLowerTokenCost()
    {
        var expensive = CreateCandidate(iteration: 0, score: 0.8, status: HarnessCandidateStatus.Evaluated);
        expensive = expensive with { TokenCost = 5000 };
        var cheap = CreateCandidate(iteration: 1, score: 0.8, status: HarnessCandidateStatus.Evaluated);
        cheap = cheap with { TokenCost = 1000 };

        await _sut.SaveAsync(expensive);
        await _sut.SaveAsync(cheap);

        var best = await _sut.GetBestAsync(_runId);

        best.Should().NotBeNull();
        best!.CandidateId.Should().Be(cheap.CandidateId,
            "when scores tie, lower token cost wins");
    }
}

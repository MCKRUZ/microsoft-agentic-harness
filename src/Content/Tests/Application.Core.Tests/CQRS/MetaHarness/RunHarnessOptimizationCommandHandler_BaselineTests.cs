using System.Text.Json;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.CQRS.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Application.Core.Tests.CQRS.MetaHarness;

/// <summary>
/// Pins that <see cref="RunHarnessOptimizationCommandHandler"/> never accepts a candidate without comparing it to
/// something already measured (issue #1075).
/// </summary>
/// <remarks>
/// A run used to start with no best candidate, and <c>IsBetter</c> returned <c>true</c> against nothing, so the first
/// proposal won whatever it scored, including when it scored below the seed it was proposed from. A resumed run did
/// the same, because the best recorded in the run manifest was never read back. Every comparison now has a measured
/// side: the evaluated seed on a fresh run, or the restored best on a resumed one.
/// </remarks>
public sealed class RunHarnessOptimizationCommandHandler_BaselineTests : IDisposable
{
    private const double SeedScore = 0.8;

    private readonly string _tempDir;
    private readonly Mock<IHarnessProposer> _proposer = new();
    private readonly Mock<IEvaluationService> _evaluator = new();
    private readonly Mock<IHarnessCandidateRepository> _repository = new();
    private readonly Mock<ISnapshotBuilder> _snapshotBuilder = new();
    private readonly Mock<IRegressionSuiteService> _regressionService = new();
    private readonly Mock<IOptionsMonitor<MetaHarnessConfig>> _configMonitor = new();
    private readonly List<HarnessCandidate> _saved = [];
    private readonly MetaHarnessConfig _cfg;

    public RunHarnessOptimizationCommandHandler_BaselineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _cfg = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            MaxIterations = 1,
            EvalTasksPath = Path.Combine(_tempDir, "eval-tasks"),
            ScoreImprovementThreshold = 0.01,
            MaxRunsToKeep = 0,
            ConsecutiveNoImprovementLimit = 0,
        };
        _configMonitor.Setup(x => x.CurrentValue).Returns(() => _cfg);

        _snapshotBuilder
            .Setup(x => x.BuildAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshot());
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback((HarnessCandidate c, CancellationToken _) => _saved.Add(c))
            .Returns(Task.CompletedTask);
        _repository
            .Setup(x => x.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());

        var emptySuite = new RegressionSuite { TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptySuite);
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] });
        _regressionService.Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptySuite);

        Directory.CreateDirectory(_cfg.EvalTasksPath);
        File.WriteAllText(Path.Combine(_cfg.EvalTasksPath, "task-1.json"),
            JsonSerializer.Serialize(new { TaskId = "task-1", Description = "d", InputPrompt = "p", Tags = Array.Empty<string>() }));
    }

    [Fact]
    public async Task Handle_FirstProposalScoresBelowTheSeed_IsNotAccepted()
    {
        ScoreSeedAndProposals(seed: SeedScore, proposal: 0.5);

        var result = await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        Assert.Null(result.BestCandidateId);
    }

    [Fact]
    public async Task Handle_FirstProposalScoresBelowTheSeed_WritesNoProposedChanges()
    {
        ScoreSeedAndProposals(seed: SeedScore, proposal: 0.5);

        var result = await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        Assert.Equal(string.Empty, result.ProposedChangesPath);
    }

    [Fact]
    public async Task Handle_FirstProposalBeatsTheSeed_IsAccepted()
    {
        ScoreSeedAndProposals(seed: 0.5, proposal: SeedScore);

        var result = await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        Assert.Equal(SeedScore, result.BestScore);
    }

    [Fact]
    public async Task Handle_ResumedRun_KeepsTheBestFoundBeforeTheRestart()
    {
        var runId = Guid.NewGuid();
        var earlierBest = EvaluatedCandidate(runId, iteration: 2, score: 0.9);
        _repository.Setup(x => x.GetAsync(earlierBest.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(earlierBest);
        WriteManifest(runId, lastCompletedIteration: 2, earlierBest.CandidateId);
        ScoreSeedAndProposals(seed: 0.5, proposal: 0.6);

        var result = await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.Equal(earlierBest.CandidateId, result.BestCandidateId);
    }

    [Fact]
    public async Task Handle_SeedCannotBeEvaluated_FailsTheRunInsteadOfComparingAgainstNothing()
    {
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                c.Iteration == 0
                    ? throw new HttpRequestException("model endpoint unavailable")
                    : new EvaluationResult(c.CandidateId, 0.5, 100, []));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildHandler().Handle(Command(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task Handle_ProposalTiesTheSeed_IsNotAccepted()
    {
        // Same score and cost: the unchanged seed wins the earlier-iteration tie-break.
        ScoreSeedAndProposals(seed: SeedScore, proposal: SeedScore);

        var result = await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        Assert.Null(result.BestCandidateId);
    }

    [Fact]
    public async Task Handle_FreshRun_ReportsTheSeedScore()
    {
        ScoreSeedAndProposals(seed: SeedScore, proposal: 0.5);

        var result = await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        Assert.Equal(SeedScore, result.SeedScore);
    }

    [Fact]
    public async Task Handle_ResumedRunWithARecordedBest_DoesNotEvaluateTheSeed()
    {
        var runId = Guid.NewGuid();
        var earlierBest = EvaluatedCandidate(runId, iteration: 2, score: 0.9);
        _repository.Setup(x => x.GetAsync(earlierBest.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(earlierBest);
        WriteManifest(runId, lastCompletedIteration: 2, earlierBest.CandidateId);
        ScoreSeedAndProposals(seed: 0.5, proposal: 0.6);

        var result = await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.Null(result.SeedScore);
    }

    [Fact]
    public async Task Handle_RecordedBestFromAnotherRun_IsNotRestored()
    {
        var runId = Guid.NewGuid();
        var foreign = EvaluatedCandidate(Guid.NewGuid(), iteration: 2, score: 0.9);
        _repository.Setup(x => x.GetAsync(foreign.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(foreign);
        WriteManifest(runId, lastCompletedIteration: 2, foreign.CandidateId);
        ScoreSeedAndProposals(seed: 0.5, proposal: 0.6);

        var result = await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.NotEqual(foreign.CandidateId, result.BestCandidateId);
    }

    [Fact]
    public async Task Handle_OwnSeed_IsSavedWithItsScore()
    {
        ScoreSeedAndProposals(seed: SeedScore, proposal: 0.5);

        await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        Assert.Contains(_saved, c => c.Iteration == 0
            && c.Status == HarnessCandidateStatus.Evaluated && c.BestScore == SeedScore);
    }

    [Fact]
    public async Task Handle_SeedImportedFromAnotherRun_IsNotOverwritten()
    {
        var imported = EvaluatedCandidate(Guid.NewGuid(), iteration: 4, score: 0.3);
        _repository.Setup(x => x.GetAsync(imported.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(imported);
        ScoreSeedAndProposals(seed: SeedScore, proposal: 0.5);

        await BuildHandler().Handle(
            new RunHarnessOptimizationCommand
            {
                OptimizationRunId = Guid.NewGuid(), MaxIterations = 1, SeedCandidateId = imported.CandidateId,
            },
            default);

        Assert.DoesNotContain(_saved, c => c.CandidateId == imported.CandidateId);
    }

    [Fact]
    public async Task Handle_ProposalTiesASeedImportedFromALaterIteration_IsNotAccepted()
    {
        // The imported seed was iteration 4 of its own run. In this run it is the starting point, so an equal
        // proposal at iteration 1 must not win the earlier-iteration tie-break against it.
        var imported = EvaluatedCandidate(Guid.NewGuid(), iteration: 4, score: SeedScore);
        _repository.Setup(x => x.GetAsync(imported.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(imported);
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, SeedScore, 100, []));

        var result = await BuildHandler().Handle(
            new RunHarnessOptimizationCommand
            {
                OptimizationRunId = Guid.NewGuid(), MaxIterations = 1, SeedCandidateId = imported.CandidateId,
            },
            default);

        Assert.Null(result.BestCandidateId);
    }

    [Fact]
    public async Task Handle_FreshRun_SeedsTheRegressionSuiteFromTheSeed()
    {
        ScoreSeedAndProposals(seed: SeedScore, proposal: 0.5);

        await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        _regressionService.Verify(x => x.PromoteAsync(It.IsAny<RegressionSuite>(),
            It.Is<EvaluationResult>(r => r.PassRate == SeedScore), null,
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_FirstWinningProposal_IsPromotedAgainstTheSeedResults()
    {
        ScoreSeedAndProposals(seed: 0.5, proposal: SeedScore);

        await BuildHandler().Handle(Command(Guid.NewGuid()), default);

        // Only tasks the seed failed are "newly fixed"; against null every passing task would be.
        _regressionService.Verify(x => x.PromoteAsync(It.IsAny<RegressionSuite>(),
            It.Is<EvaluationResult>(r => r.PassRate == SeedScore),
            It.Is<EvaluationResult?>(r => r != null && r.PassRate == 0.5),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    private RunHarnessOptimizationCommandHandler BuildHandler() =>
        new(_proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object, _configMonitor.Object,
            Mock.Of<ILogger<RunHarnessOptimizationCommandHandler>>(), new PlainDirectoryCreator());

    private static RunHarnessOptimizationCommand Command(Guid runId, int maxIterations = 1) =>
        new() { OptimizationRunId = runId, MaxIterations = maxIterations };

    // The seed is the only candidate at iteration 0; every proposal is at iteration 1 or later.
    private void ScoreSeedAndProposals(double seed, double proposal) =>
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, c.Iteration == 0 ? seed : proposal, 100, []));

    private void WriteManifest(Guid runId, int lastCompletedIteration, Guid bestCandidateId)
    {
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "run_manifest.json"), JsonSerializer.Serialize(new
        {
            optimizationRunId = runId,
            lastCompletedIteration,
            bestCandidateId,
            startedAt = DateTimeOffset.UtcNow,
            writeCompleted = true,
        }));
    }

    private static HarnessCandidate EvaluatedCandidate(Guid runId, int iteration, double score) => new()
    {
        CandidateId = Guid.NewGuid(),
        OptimizationRunId = runId,
        ParentCandidateId = Guid.NewGuid(),
        Iteration = iteration,
        CreatedAt = DateTimeOffset.UtcNow,
        Snapshot = BuildSnapshot(),
        BestScore = score,
        TokenCost = 100,
        Status = HarnessCandidateStatus.Evaluated,
    };

    private static HarnessSnapshot BuildSnapshot() => new()
    {
        SkillFileSnapshots = new Dictionary<string, string>(),
        SystemPromptSnapshot = "prompt",
        ConfigSnapshot = new Dictionary<string, string>(),
        SnapshotManifest = [],
    };

    private static HarnessProposal BuildProposal() => new()
    {
        ProposedSkillChanges = new Dictionary<string, string>(),
        ProposedConfigChanges = new Dictionary<string, string>(),
        Reasoning = "reasoning",
    };
}

using System.Text.Json;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.CQRS.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Pins that a resumed optimization run continues where it stopped, read back through the real
/// <see cref="FileSystemHarnessCandidateRepository"/> rather than a mock that always finds the record
/// (issues #1075 and #1077).
/// </summary>
/// <remarks>
/// A restart must keep four things the uninterrupted run would have had: the best candidate already accepted, the
/// candidate the next proposal is built from, the run's one seed (so it is neither rebuilt nor measured again), and the
/// count of iterations since the last improvement. A mock repository proves the restore logic but not that the real
/// store returns the latest save of each id; if it did not, the restore would quietly fall back to a fresh seed.
/// </remarks>
public sealed class RunHarnessOptimizationResumeIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetaHarnessConfig _cfg;
    private readonly FileSystemHarnessCandidateRepository _repository;
    private readonly Mock<IEvaluationService> _evaluator = new();
    private readonly List<HarnessCandidate> _evaluated = [];
    private readonly Dictionary<int, List<Guid>> _priorIdsSeenByIteration = [];

    // By default the seed (iteration 0) scores 0.5, the first proposal 0.9, and every later proposal 0.6: better than
    // the seed, worse than the best accepted before the restart.
    private Func<int, double> _passRateByIteration = iteration => iteration switch { 0 => 0.5, 1 => 0.9, _ => 0.6 };

    // When set, evaluating a candidate at this iteration cancels the run, as a host shutting down would.
    private int? _interruptAtIteration;

    public RunHarnessOptimizationResumeIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"optimization-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _cfg = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            EvalTasksPath = Path.Combine(_tempDir, "eval-tasks"),
            ScoreImprovementThreshold = 0.01,
            MaxRunsToKeep = 0,
            ConsecutiveNoImprovementLimit = 0,
        };
        Directory.CreateDirectory(_cfg.EvalTasksPath);
        File.WriteAllText(Path.Combine(_cfg.EvalTasksPath, "task-1.json"),
            JsonSerializer.Serialize(new { TaskId = "task-1", Description = "d", InputPrompt = "p", Tags = Array.Empty<string>() }));

        _repository = new FileSystemHarnessCandidateRepository(
            Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(o => o.CurrentValue == _cfg),
            NullLogger<FileSystemHarnessCandidateRepository>.Instance);

        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
            {
                _evaluated.Add(c);
                if (c.Iteration == _interruptAtIteration)
                    throw new OperationCanceledException("host stopped mid-evaluation");
                return new EvaluationResult(c.CandidateId, _passRateByIteration(c.Iteration), 100, []);
            });
    }

    [Fact]
    public async Task Handle_ResumedRun_KeepsTheBestAcceptedBeforeTheRestart()
    {
        var runId = Guid.NewGuid();
        var beforeRestart = await BuildHandler().Handle(Command(runId, maxIterations: 1), default);

        var afterRestart = await BuildHandler().Handle(Command(runId, maxIterations: 2), default);

        Assert.Equal(beforeRestart.BestCandidateId, afterRestart.BestCandidateId);
    }

    [Fact]
    public async Task Handle_ResumedRun_DoesNotMeasureTheSeedAgain()
    {
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 1), default);
        _evaluated.Clear();

        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);

        Assert.Equal([2], _evaluated.Select(c => c.Iteration));
    }

    [Fact]
    public async Task Handle_ResumedRun_BuildsTheNextProposalFromTheLastCandidate()
    {
        // Within a run every proposal is built from the latest evaluated candidate, so the first proposal after a
        // restart must be built from the last one before it, not from a newly built seed.
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);
        var lastBeforeRestart = _evaluated.Single(c => c.Iteration == 2);

        await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.Equal(lastBeforeRestart.CandidateId, _evaluated.Single(c => c.Iteration == 3).ParentCandidateId);
    }

    [Fact]
    public async Task Handle_ResumedRunWithNoAcceptedProposal_DoesNotMeasureTheSeedAgain()
    {
        _passRateByIteration = iteration => iteration switch { 0 => 0.8, 1 or 2 => 0.5, _ => 0.7 };
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);
        _evaluated.Clear();

        await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.Equal([3], _evaluated.Select(c => c.Iteration));
    }

    [Fact]
    public async Task Handle_ResumedRunWithNoAcceptedProposal_StillJudgesAgainstTheSeed()
    {
        // The proposal after the restart (0.7) is below the seed (0.8), so the restored seed must still be the bar.
        _passRateByIteration = iteration => iteration switch { 0 => 0.8, 1 or 2 => 0.5, _ => 0.7 };
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);

        var afterRestart = await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.Null(afterRestart.BestCandidateId);
    }

    [Fact]
    public async Task Handle_ResumedRun_KeepsOneSeed()
    {
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);

        await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        var candidates = await _repository.ListAsync(runId);
        Assert.Single(candidates, c => c.Iteration == 0);
    }

    [Fact]
    public async Task Handle_ResumedRun_CarriesTheNoImprovementCountOverTheRestart()
    {
        // Limit 3. Two iterations without improvement before the restart, so the run stops after one more, exactly as
        // an uninterrupted run would.
        _cfg.ConsecutiveNoImprovementLimit = 3;
        _passRateByIteration = iteration => iteration == 0 ? 0.8 : 0.5;
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);

        var afterRestart = await BuildHandler().Handle(Command(runId, maxIterations: 10), default);

        Assert.Equal(1, afterRestart.IterationCount);
    }

    [Fact]
    public async Task Handle_ResumedRunThatAlreadyStoppedEarly_RunsNoMoreIterations()
    {
        // Limit 3, and three iterations without improvement: the run stopped early. Resuming it with more iterations
        // must stop where the uninterrupted run did, not grant a fresh allowance.
        _cfg.ConsecutiveNoImprovementLimit = 3;
        _passRateByIteration = iteration => iteration == 0 ? 0.8 : 0.5;
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 3), default);
        _evaluated.Clear();

        await BuildHandler().Handle(Command(runId, maxIterations: 10), default);

        Assert.Empty(_evaluated);
    }

    [Fact]
    public async Task Handle_RunInterruptedDuringItsFirstIteration_MeasuresTheSeedOnce()
    {
        var runId = Guid.NewGuid();
        await InterruptFirstIterationAsync(runId);

        await BuildHandler().Handle(Command(runId, maxIterations: 1), default);

        Assert.Single(_evaluated, c => c.Iteration == 0);
    }

    [Fact]
    public async Task Handle_RunInterruptedDuringItsFirstIteration_KeepsOneSeed()
    {
        var runId = Guid.NewGuid();
        await InterruptFirstIterationAsync(runId);

        await BuildHandler().Handle(Command(runId, maxIterations: 1), default);

        Assert.Single(await _repository.ListAsync(runId), c => c.Iteration == 0);
    }

    [Fact]
    public async Task Handle_ResumedRun_TellsTheProposerAboutTheEarlierCandidates()
    {
        var runId = Guid.NewGuid();
        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);
        var beforeRestart = _evaluated.Where(c => c.Iteration > 0).Select(c => c.CandidateId).ToList();

        await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.Equal(beforeRestart, _priorIdsSeenByIteration[3]);
    }

    [Fact]
    public async Task Handle_ResumedRunWithAnOldManifest_MeasuresANewSeedAsBefore()
    {
        // A manifest written before the seed and working candidate were recorded holds neither, so the run cannot
        // recover them and falls back to building and measuring a seed, as every resume did before #1077.
        var runId = Guid.NewGuid();
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "run_manifest.json"), JsonSerializer.Serialize(new
        {
            optimizationRunId = runId,
            lastCompletedIteration = 2,
            bestCandidateId = (Guid?)null,
            startedAt = DateTimeOffset.UtcNow,
            writeCompleted = true,
        }));

        await BuildHandler().Handle(Command(runId, maxIterations: 3), default);

        Assert.Equal([0, 3], _evaluated.Select(c => c.Iteration));
    }

    private RunHarnessOptimizationCommandHandler BuildHandler()
    {
        var proposer = new Mock<IHarnessProposer>();
        proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            // PriorCandidateIds is a live view of the handler's list, so copy it as it was when the proposer saw it.
            .Callback((HarnessProposerContext ctx, CancellationToken _) =>
                _priorIdsSeenByIteration[ctx.Iteration] = [.. ctx.PriorCandidateIds])
            .ReturnsAsync(new HarnessProposal
            {
                ProposedSkillChanges = new Dictionary<string, string>(),
                ProposedConfigChanges = new Dictionary<string, string>(),
                Reasoning = "reasoning",
            });

        var snapshotBuilder = new Mock<ISnapshotBuilder>();
        snapshotBuilder
            .Setup(x => x.BuildAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessSnapshot
            {
                SkillFileSnapshots = new Dictionary<string, string>(),
                SystemPromptSnapshot = "prompt",
                ConfigSnapshot = new Dictionary<string, string>(),
                SnapshotManifest = [],
            });

        var suite = new RegressionSuite { TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var regression = new Mock<IRegressionSuiteService>();
        regression.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(suite);
        regression.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] });
        regression.Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(suite);

        return new RunHarnessOptimizationCommandHandler(
            proposer.Object, _evaluator.Object, _repository, snapshotBuilder.Object, regression.Object,
            Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(o => o.CurrentValue == _cfg),
            NullLogger<RunHarnessOptimizationCommandHandler>.Instance,
            new PlainDirectoryCreator());
    }

    // The seed is measured, then the host stops while the first proposal is evaluated, before any iteration completes.
    private async Task InterruptFirstIterationAsync(Guid runId)
    {
        _interruptAtIteration = 1;
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => BuildHandler().Handle(Command(runId, maxIterations: 1), default));
        _interruptAtIteration = null;
    }

    private static RunHarnessOptimizationCommand Command(Guid runId, int maxIterations) =>
        new() { OptimizationRunId = runId, MaxIterations = maxIterations };

    public void Dispose()
    {
        _repository.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

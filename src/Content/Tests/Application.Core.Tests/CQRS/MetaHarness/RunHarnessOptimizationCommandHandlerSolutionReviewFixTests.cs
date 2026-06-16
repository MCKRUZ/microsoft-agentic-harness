using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.CQRS.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace Application.Core.Tests.CQRS.MetaHarness;

/// <summary>
/// Regression tests for the solution-review finding "Regression gate is bypassed in the final
/// optimization output — _proposed/ snapshot can be a gate-failing candidate".
/// </summary>
/// <remarks>
/// The handler previously selected the run's best via
/// <see cref="IHarnessCandidateRepository.GetBestAsync"/>, which orders purely by pass rate among
/// all <see cref="HarnessCandidateStatus.Evaluated"/> candidates and has no knowledge of
/// regression-gate outcomes. A candidate with the highest pass rate that FAILED the regression
/// gate would still be written to <c>_proposed/</c> and returned as the run's best, making the
/// gate decorative for the artifact consumers actually apply. The fix selects the
/// gate-validated in-memory best instead.
/// </remarks>
public sealed class RunHarnessOptimizationCommandHandlerSolutionReviewFixTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IHarnessProposer> _proposer = new();
    private readonly Mock<IEvaluationService> _evaluator = new();
    private readonly Mock<IHarnessCandidateRepository> _repository = new();
    private readonly Mock<ISnapshotBuilder> _snapshotBuilder = new();
    private readonly Mock<IRegressionSuiteService> _regressionService = new();
    private readonly Mock<IOptionsMonitor<MetaHarnessConfig>> _configMonitor = new();
    private readonly Mock<ILogger<RunHarnessOptimizationCommandHandler>> _logger = new();
    private readonly List<HarnessCandidate> _savedCandidates = new();
    private MetaHarnessConfig _cfg;

    public RunHarnessOptimizationCommandHandlerSolutionReviewFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _cfg = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            MaxIterations = 2,
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

        // Capture every saved candidate so the test can identify gated vs gate-failing ones.
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Returns((HarnessCandidate c, CancellationToken _) =>
            {
                _savedCandidates.Add(c);
                return Task.CompletedTask;
            });
        _repository
            .Setup(x => x.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private RunHarnessOptimizationCommandHandler BuildHandler() =>
        new(_proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object, _configMonitor.Object, _logger.Object);

    private static HarnessSnapshot BuildSnapshot() => new()
    {
        SkillFileSnapshots = new Dictionary<string, string> { ["SKILL.md"] = "content" },
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

    private void CreateEvalTaskFile(string taskId = "task-1")
    {
        Directory.CreateDirectory(_cfg.EvalTasksPath);
        File.WriteAllText(Path.Combine(_cfg.EvalTasksPath, $"{taskId}.json"),
            JsonSerializer.Serialize(new { TaskId = taskId, Description = "d", InputPrompt = "p", Tags = Array.Empty<string>() }));
    }

    private static RegressionSuite EmptySuite() => new()
    {
        TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Iteration 1 passes the regression gate; iteration 2 has a strictly higher pass rate but
    /// FAILS the gate. The repository's ungated GetBestAsync would return iteration 2 (highest
    /// pass rate). The handler must instead return iteration 1 — the gate-validated best — so the
    /// proposed artifact matches the documented promotion contract.
    /// </summary>
    [Fact]
    public async Task Handle_HighestPassRateCandidateFailedGate_ReturnsGatedBestNotUngatedRepositoryPick()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());

        // iter1 pass rate 0.6, iter2 pass rate 0.9 (strictly higher → IsBetter true on raw score)
        var evalCall = 0;
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
            {
                evalCall++;
                var passRate = evalCall == 1 ? 0.6 : 0.9;
                return new EvaluationResult(c.CandidateId, passRate, 100, []);
            });

        var suite = EmptySuite();
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(suite);

        // iter1 passes gate, iter2 fails gate
        var checkCall = 0;
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(() =>
            {
                checkCall++;
                return checkCall == 1
                    ? new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] }
                    : new RegressionCheckResult { Passed = false, PassRate = 0.0, FailedTaskIds = ["task-1"] };
            });
        _regressionService.Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(suite);

        // The real FileSystemHarnessCandidateRepository.GetBestAsync orders by pass rate and would
        // therefore return the gate-FAILING iteration 2 candidate. Wire the mock to mirror that so
        // the old code path (which used GetBestAsync) would have produced the regressive artifact.
        _repository
            .Setup(x => x.GetBestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _savedCandidates
                .Where(c => c.Status == HarnessCandidateStatus.Evaluated)
                .OrderByDescending(c => c.BestScore ?? 0.0)
                .FirstOrDefault());

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(
            new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 2 }, default);

        // Assert: the gate-validated best is iteration 1 (the only candidate that passed the gate),
        // NOT iteration 2 which had the higher pass rate but failed the gate.
        var iter1Evaluated = _savedCandidates.Single(
            c => c.Iteration == 1 && c.Status == HarnessCandidateStatus.Evaluated);
        var iter2Evaluated = _savedCandidates.Single(
            c => c.Iteration == 2 && c.Status == HarnessCandidateStatus.Evaluated);

        Assert.Equal(iter1Evaluated.CandidateId, result.BestCandidateId);
        Assert.NotEqual(iter2Evaluated.CandidateId, result.BestCandidateId);
        Assert.Equal(0.6, result.BestScore);

        // The _proposed/ snapshot must reflect iteration 1's gated candidate path, never empty
        // (a gated best exists) and never the gate-failing candidate.
        Assert.NotEqual(string.Empty, result.ProposedChangesPath);
        Assert.True(Directory.Exists(result.ProposedChangesPath));
    }

    /// <summary>
    /// When NO candidate ever clears the regression gate, the handler must not emit a proposed
    /// artifact at all — returning the ungated repository pick here would publish a gate-failing
    /// (regressive) snapshot. The result must report no best candidate and an empty proposed path.
    /// </summary>
    [Fact]
    public async Task Handle_NoCandidatePassesGate_WritesNoProposedSnapshotAndReportsNoBest()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.9, 100, []));

        var suite = EmptySuite();
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(suite);

        // Every gate check fails.
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = false, PassRate = 0.0, FailedTaskIds = ["task-1"] });

        // Repository would still surface an Evaluated candidate by pass rate.
        _repository
            .Setup(x => x.GetBestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _savedCandidates
                .Where(c => c.Status == HarnessCandidateStatus.Evaluated)
                .OrderByDescending(c => c.BestScore ?? 0.0)
                .FirstOrDefault());

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(
            new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 2 }, default);

        // Assert: no gated best, no proposed artifact emitted.
        Assert.Null(result.BestCandidateId);
        Assert.Equal(string.Empty, result.ProposedChangesPath);
        _regressionService.Verify(x => x.PromoteAsync(
            It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
            It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

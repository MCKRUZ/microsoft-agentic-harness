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
/// Tests for regression suite gating in <see cref="RunHarnessOptimizationCommandHandler"/>.
/// </summary>
public sealed class RunHarnessOptimizationCommandHandler_RegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IHarnessProposer> _proposer = new();
    private readonly Mock<IEvaluationService> _evaluator = new();
    private readonly Mock<IHarnessCandidateRepository> _repository = new();
    private readonly Mock<ISnapshotBuilder> _snapshotBuilder = new();
    private readonly Mock<IRegressionSuiteService> _regressionService = new();
    private readonly Mock<IOptionsMonitor<MetaHarnessConfig>> _configMonitor = new();
    private readonly Mock<ILogger<RunHarnessOptimizationCommandHandler>> _logger = new();
    private MetaHarnessConfig _cfg;

    public RunHarnessOptimizationCommandHandler_RegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _cfg = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            MaxIterations = 3,
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
            .Returns(Task.CompletedTask);
        _repository
            .Setup(x => x.GetBestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate?)null);
        _repository
            .Setup(x => x.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private RunHarnessOptimizationCommandHandler BuildHandler() =>
        new(_proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object, _configMonitor.Object, _logger.Object);

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

    [Fact]
    public async Task Handle_RegressionGatePasses_AcceptsNewBest()
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
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] });
        _regressionService.Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(suite);

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 1 }, default);

        // Assert: PromoteAsync was called (suite was updated after accepting new best)
        _regressionService.Verify(x => x.PromoteAsync(
            It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
            It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_RegressionGateFails_RejectsCandidate_KeepsPreviousBest()
    {
        // Arrange: iter1 passes gate → new best; iter2 fails gate → rejected
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.9, 100, []));

        var suite = EmptySuite();
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(suite);

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

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(
            new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 2 }, default);

        // Assert: PromoteAsync called once (only iter1 passed the gate)
        _regressionService.Verify(x => x.PromoteAsync(
            It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
            It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_EmptyRegressionSuite_AlwaysPasses()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        var callCount = 0;
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
            {
                callCount++;
                // Strictly increasing scores so each iteration is accepted as new best
                return new EvaluationResult(c.CandidateId, 0.6 + callCount * 0.1, 100, []);
            });

        var suite = EmptySuite();
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(suite);
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] });
        _regressionService.Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(suite);

        var handler = BuildHandler();

        // Act — 3 iterations with improving scores; empty suite should never block any
        var result = await handler.Handle(
            new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 3 }, default);

        // Assert: PromoteAsync called 3 times — empty suite passed the gate every time
        _regressionService.Verify(x => x.PromoteAsync(
            It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
            It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task Handle_PromotesTasksAfterAcceptingNewBest()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.9, 100, []));

        EvaluationResult? capturedCurrent = null;
        var suite = EmptySuite();
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(suite);
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] });
        _regressionService
            .Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<RegressionSuite, EvaluationResult, EvaluationResult?, string, CancellationToken>(
                (_, current, _, _, _) => capturedCurrent = current)
            .ReturnsAsync(suite);

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 1 }, default);

        // Assert: PromoteAsync received the current eval result (not null)
        Assert.NotNull(capturedCurrent);
        Assert.Equal(0.9, capturedCurrent.PassRate);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

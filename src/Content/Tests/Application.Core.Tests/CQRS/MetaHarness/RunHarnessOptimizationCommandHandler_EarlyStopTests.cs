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
/// Tests for consecutive no-improvement early stopping in <see cref="RunHarnessOptimizationCommandHandler"/>.
/// </summary>
public sealed class RunHarnessOptimizationCommandHandler_EarlyStopTests : IDisposable
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

    public RunHarnessOptimizationCommandHandler_EarlyStopTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _cfg = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            MaxIterations = 10, // high — early stop should trigger before this
            EvalTasksPath = Path.Combine(_tempDir, "eval-tasks"),
            ScoreImprovementThreshold = 0.01,
            MaxRunsToKeep = 0,
            ConsecutiveNoImprovementLimit = 3,
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

        var emptySuite = new RegressionSuite { TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(emptySuite);
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] });
        _regressionService.Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptySuite);
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

    [Fact]
    public async Task Handle_ConsecutiveNoImprovement_StopsEarlyAtLimit()
    {
        // Arrange: limit=3, MaxIterations=10, all iterations produce same score (no improvement)
        // Iter 1: IsBetter(vs null) = true → accepted as best → reset counter to 0
        // Iter 2: IsBetter(vs iter1, same score/cost, iter2>iter1) = false → counter=1
        // Iter 3: counter=2
        // Iter 4: counter=3 → early stop
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 10 }, default);

        // Assert: stopped before MaxIterations=10
        Assert.True(result.IterationCount < 10, $"Expected early stop but ran {result.IterationCount} iterations");
        Assert.Equal("no_improvement", result.EarlyStopReason);
    }

    [Fact]
    public async Task Handle_ImprovementResetsCounter_ContinuesLoop()
    {
        // Arrange: limit=3, iter1=0.5 (accepted), iter2-4 stuck at 0.5 (no improvement → counter hits 3)
        // With iter1 accepted (counter reset), then 3 consecutive no-improvement → stop at iter4
        _cfg.ScoreImprovementThreshold = 0.1;
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());

        var callCount = 0;
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
            {
                callCount++;
                // Iter 1 starts with no best → IsBetter = true → accepted, counter=0
                // Iter 2: 0.6 > 0.5+0.1 threshold, so IsBetter=true → accepted, counter=0
                // Iter 3-5: 0.6 same, no improvement → counter hits 3 → early stop at iter 5
                var score = callCount <= 2 ? callCount * 0.3 : 0.6;
                return new EvaluationResult(c.CandidateId, score, 100, []);
            });

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 10 }, default);

        // Assert: did not run all 10, stopped early
        Assert.True(result.IterationCount < 10);
        Assert.Equal("no_improvement", result.EarlyStopReason);
    }

    [Fact]
    public async Task Handle_LimitZero_DisablesEarlyStop()
    {
        // Arrange: limit=0 (disabled), all iterations produce same score
        _cfg.ConsecutiveNoImprovementLimit = 0;
        _cfg.MaxIterations = 5;
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.5, 100, []));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 5 }, default);

        // Assert: all 5 iterations ran, no early stop
        Assert.Equal(5, result.IterationCount);
        Assert.Null(result.EarlyStopReason);
    }

    [Fact]
    public async Task Handle_EarlyStop_SetsEarlyStopReasonInResult()
    {
        // Arrange
        _cfg.ConsecutiveNoImprovementLimit = 2;
        _cfg.ScoreImprovementThreshold = 0.5; // very high threshold — nothing will be "better"
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.5, 100, []));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 10 }, default);

        // Assert
        Assert.Equal("no_improvement", result.EarlyStopReason);
    }

    [Fact]
    public async Task Handle_FailedIterationsCountAsNoImprovement()
    {
        // Arrange: limit=3, all proposer calls throw → 3 failures → early stop
        _cfg.ConsecutiveNoImprovementLimit = 3;
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Application.AI.Common.Exceptions.HarnessProposalParsingException("bad"));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 10 }, default);

        // Assert: stopped at iteration 3, not 10
        Assert.Equal(3, result.IterationCount);
        Assert.Equal("no_improvement", result.EarlyStopReason);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

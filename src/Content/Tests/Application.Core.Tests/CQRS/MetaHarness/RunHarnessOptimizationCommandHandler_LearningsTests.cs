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
/// Tests for learnings log read/write in <see cref="RunHarnessOptimizationCommandHandler"/>.
/// </summary>
public sealed class RunHarnessOptimizationCommandHandler_LearningsTests : IDisposable
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

    public RunHarnessOptimizationCommandHandler_LearningsTests()
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

    private static HarnessProposal BuildProposal(string? learnings = null) => new()
    {
        ProposedSkillChanges = new Dictionary<string, string>(),
        ProposedConfigChanges = new Dictionary<string, string>(),
        Reasoning = "test reasoning",
        Learnings = learnings,
    };

    private void CreateEvalTaskFile(string taskId = "task-1")
    {
        Directory.CreateDirectory(_cfg.EvalTasksPath);
        File.WriteAllText(Path.Combine(_cfg.EvalTasksPath, $"{taskId}.json"),
            JsonSerializer.Serialize(new { TaskId = taskId, Description = "d", InputPrompt = "p", Tags = Array.Empty<string>() }));
    }

    [Fact]
    public async Task Handle_WritesLearningsMdAfterEachIteration()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal("observed pattern"));
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 2 }, default);

        // Assert: learnings.md exists and contains entries for both iterations
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var learningsPath = Path.Combine(runDir, "learnings.md");
        Assert.True(File.Exists(learningsPath));
        var content = await File.ReadAllTextAsync(learningsPath);
        Assert.Contains("## Iteration 1", content);
        Assert.Contains("## Iteration 2", content);
        Assert.Contains("observed pattern", content);
    }

    [Fact]
    public async Task Handle_WritesLearningsMdOnFailedIteration()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        var callCount = 0;
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) throw new Application.AI.Common.Exceptions.HarnessProposalParsingException("bad json");
                return BuildProposal();
            });
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 2 }, default);

        // Assert: learnings.md contains a FAILED entry for iter 1 with the exception message prefix
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var content = await File.ReadAllTextAsync(Path.Combine(runDir, "learnings.md"));
        Assert.Contains("FAILED", content);
        Assert.Contains("Failed to parse", content); // HarnessProposalParsingException default message
    }

    [Fact]
    public async Task Handle_PassesPriorLearningsToProposerContext()
    {
        // Arrange: 2 iterations; verify iter 2 receives iter 1's learnings
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        var capturedContexts = new List<HarnessProposerContext>();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessProposerContext, CancellationToken>((ctx, _) => capturedContexts.Add(ctx))
            .ReturnsAsync(BuildProposal("iter learnings text"));
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 2 }, default);

        // Assert: iter 2 context has learnings from iter 1
        Assert.Equal(2, capturedContexts.Count);
        Assert.Null(capturedContexts[0].PriorLearnings); // iter 1: no prior learnings
        Assert.NotNull(capturedContexts[1].PriorLearnings); // iter 2: should have learnings
        Assert.Contains("iter learnings text", capturedContexts[1].PriorLearnings);
    }

    [Fact]
    public async Task Handle_FirstIteration_PriorLearningsIsNull()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        HarnessProposerContext? capturedCtx = null;
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessProposerContext, CancellationToken>((ctx, _) => capturedCtx ??= ctx)
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 1 }, default);

        // Assert
        Assert.NotNull(capturedCtx);
        Assert.Null(capturedCtx.PriorLearnings);
    }

    [Fact]
    public async Task Handle_LearningsMdGrowsCumulatively()
    {
        // Arrange: 3 iterations — content should accumulate across iterations
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        _proposer.Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator.Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand { OptimizationRunId = runId, MaxIterations = 3 }, default);

        // Assert: all three iteration headers present
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var content = await File.ReadAllTextAsync(Path.Combine(runDir, "learnings.md"));
        Assert.Contains("## Iteration 1", content);
        Assert.Contains("## Iteration 2", content);
        Assert.Contains("## Iteration 3", content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

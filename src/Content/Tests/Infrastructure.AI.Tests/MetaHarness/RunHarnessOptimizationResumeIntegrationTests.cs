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
/// Pins that a resumed optimization run keeps the best candidate it accepted before the restart, read back through the
/// real <see cref="FileSystemHarnessCandidateRepository"/> rather than a mock that always finds the record (issue #1075).
/// </summary>
/// <remarks>
/// The handler restores the best by the id in the run manifest and only accepts a record that is evaluated, scored and
/// from the same run. A mock repository proves that logic but not that the real store returns the latest save of that
/// id; if it did not, the restore would quietly fall back to measuring the seed and the earlier best would be lost.
/// </remarks>
public sealed class RunHarnessOptimizationResumeIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetaHarnessConfig _cfg;
    private readonly FileSystemHarnessCandidateRepository _repository;
    private readonly Mock<IEvaluationService> _evaluator = new();
    private readonly List<int> _evaluatedIterations = [];

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

        // The seed (iteration 0) scores 0.5, the first proposal 0.9, and the proposal made after the restart 0.6:
        // better than the seed, worse than the best accepted before the restart.
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
            {
                _evaluatedIterations.Add(c.Iteration);
                var passRate = c.Iteration switch { 0 => 0.5, 1 => 0.9, _ => 0.6 };
                return new EvaluationResult(c.CandidateId, passRate, 100, []);
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
        _evaluatedIterations.Clear();

        await BuildHandler().Handle(Command(runId, maxIterations: 2), default);

        Assert.Equal([2], _evaluatedIterations);
    }

    private RunHarnessOptimizationCommandHandler BuildHandler()
    {
        var proposer = new Mock<IHarnessProposer>();
        proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
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

    private static RunHarnessOptimizationCommand Command(Guid runId, int maxIterations) =>
        new() { OptimizationRunId = runId, MaxIterations = maxIterations };

    public void Dispose()
    {
        _repository.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

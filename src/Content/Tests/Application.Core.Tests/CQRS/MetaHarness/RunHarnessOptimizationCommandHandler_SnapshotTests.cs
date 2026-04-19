using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.CQRS.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace Application.Core.Tests.CQRS.MetaHarness;

/// <summary>
/// Tests for snapshot building, proposed snapshot writing, and seed candidate resolution
/// in <see cref="RunHarnessOptimizationCommandHandler"/>.
/// </summary>
public sealed class RunHarnessOptimizationCommandHandler_SnapshotTests : IDisposable
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

    public RunHarnessOptimizationCommandHandler_SnapshotTests()
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

        var emptySuite = new RegressionSuite
        {
            TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow
        };
        _regressionService.Setup(x => x.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptySuite);
        _regressionService.Setup(x => x.Check(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>()))
            .Returns(new RegressionCheckResult { Passed = true, PassRate = 1.0, FailedTaskIds = [] });
        _regressionService.Setup(x => x.PromoteAsync(It.IsAny<RegressionSuite>(), It.IsAny<EvaluationResult>(),
                It.IsAny<EvaluationResult?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptySuite);
    }

    private RunHarnessOptimizationCommandHandler BuildHandler() =>
        new(_proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object, _configMonitor.Object, _logger.Object);

    private static HarnessSnapshot BuildSnapshot(Dictionary<string, string>? skills = null) => new()
    {
        SkillFileSnapshots = skills ?? new Dictionary<string, string>(),
        SystemPromptSnapshot = "prompt",
        ConfigSnapshot = new Dictionary<string, string>(),
        SnapshotManifest = [],
    };

    private void CreateEvalTaskFile(string taskId = "task-1")
    {
        Directory.CreateDirectory(_cfg.EvalTasksPath);
        File.WriteAllText(Path.Combine(_cfg.EvalTasksPath, $"{taskId}.json"),
            JsonSerializer.Serialize(new
            {
                TaskId = taskId, Description = "d", InputPrompt = "p",
                Tags = Array.Empty<string>()
            }));
    }

    [Fact]
    public async Task Handle_ProposalWithSkillChanges_MergesIntoSnapshot()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessProposal
            {
                ProposedSkillChanges = new Dictionary<string, string>
                {
                    ["SKILL.md"] = "# Updated skill"
                },
                ProposedConfigChanges = new Dictionary<string, string>(),
                Reasoning = "improve skill"
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand
        {
            OptimizationRunId = runId,
            MaxIterations = 1
        }, default);

        // Assert: iteration 1 candidate (not seed at iteration 0) has the merged skill
        var proposed = savedCandidates.FirstOrDefault(c =>
            c.Status == HarnessCandidateStatus.Proposed && c.Iteration > 0);
        proposed.Should().NotBeNull();
        proposed!.Snapshot.SkillFileSnapshots.Should().ContainKey("SKILL.md");
        proposed.Snapshot.SkillFileSnapshots["SKILL.md"].Should().Be("# Updated skill");
    }

    [Fact]
    public async Task Handle_ProposalWithConfigChanges_MergesIntoSnapshot()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessProposal
            {
                ProposedSkillChanges = new Dictionary<string, string>(),
                ProposedConfigChanges = new Dictionary<string, string>
                {
                    ["temperature"] = "0.7"
                },
                Reasoning = "tune config"
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand
        {
            OptimizationRunId = runId,
            MaxIterations = 1
        }, default);

        // Assert: iteration 1 candidate has the config change
        var proposed = savedCandidates.FirstOrDefault(c =>
            c.Status == HarnessCandidateStatus.Proposed && c.Iteration > 0);
        proposed.Should().NotBeNull();
        proposed!.Snapshot.ConfigSnapshot.Should().ContainKey("temperature");
        proposed.Snapshot.ConfigSnapshot["temperature"].Should().Be("0.7");
    }

    [Fact]
    public async Task Handle_ProposalWithSystemPromptChange_UpdatesSystemPrompt()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessProposal
            {
                ProposedSkillChanges = new Dictionary<string, string>(),
                ProposedConfigChanges = new Dictionary<string, string>(),
                ProposedSystemPromptChange = "You are a helpful assistant.",
                Reasoning = "improve prompt"
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand
        {
            OptimizationRunId = runId,
            MaxIterations = 1
        }, default);

        // Assert: iteration 1 candidate has the updated system prompt
        var proposed = savedCandidates.FirstOrDefault(c =>
            c.Status == HarnessCandidateStatus.Proposed && c.Iteration > 0);
        proposed.Should().NotBeNull();
        proposed!.Snapshot.SystemPromptSnapshot.Should().Be("You are a helpful assistant.");
    }

    [Fact]
    public async Task Handle_NoBestCandidate_ProposedChangesPathIsEmpty()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessProposal
            {
                ProposedSkillChanges = new Dictionary<string, string>(),
                ProposedConfigChanges = new Dictionary<string, string>(),
                Reasoning = "test"
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate?)null);

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(new RunHarnessOptimizationCommand
        {
            OptimizationRunId = runId,
            MaxIterations = 1
        }, default);

        // Assert
        result.ProposedChangesPath.Should().BeEmpty();
        result.BestCandidateId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WritesSummaryMarkdown_AfterCompletion()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        var candidate = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = runId,
            Iteration = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = HarnessCandidateStatus.Evaluated,
            BestScore = 0.85,
            TokenCost = 150,
            Snapshot = BuildSnapshot()
        };

        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);
        _repository
            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidate });

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessProposal
            {
                ProposedSkillChanges = new Dictionary<string, string>(),
                ProposedConfigChanges = new Dictionary<string, string>(),
                Reasoning = "test"
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.85, 150, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand
        {
            OptimizationRunId = runId,
            MaxIterations = 1
        }, default);

        // Assert
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var summaryPath = Path.Combine(runDir, "summary.md");
        File.Exists(summaryPath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(summaryPath);
        content.Should().Contain("Optimization Run Summary");
        content.Should().Contain("Evaluated");
    }

    [Fact]
    public async Task Handle_SeedCandidateIdNotFound_ThrowsInvalidOperation()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var seedId = Guid.NewGuid();
        CreateEvalTaskFile();

        _repository
            .Setup(x => x.GetAsync(seedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate?)null);

        var handler = BuildHandler();

        // Act
        var act = () => handler.Handle(new RunHarnessOptimizationCommand
        {
            OptimizationRunId = runId,
            SeedCandidateId = seedId
        }, default);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{seedId}*not found*");
    }

    [Fact]
    public async Task Handle_SeedCandidateIdFound_UsesThatCandidate()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var seedId = Guid.NewGuid();
        CreateEvalTaskFile();

        var seedCandidate = new HarnessCandidate
        {
            CandidateId = seedId,
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 5,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = HarnessCandidateStatus.Evaluated,
            Snapshot = BuildSnapshot(new Dictionary<string, string>
            {
                ["SKILL.md"] = "# Seed skill"
            })
        };

        _repository
            .Setup(x => x.GetAsync(seedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seedCandidate);

        HarnessProposerContext? capturedContext = null;
        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessProposerContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new HarnessProposal
            {
                ProposedSkillChanges = new Dictionary<string, string>(),
                ProposedConfigChanges = new Dictionary<string, string>(),
                Reasoning = "test"
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(new RunHarnessOptimizationCommand
        {
            OptimizationRunId = runId,
            SeedCandidateId = seedId,
            MaxIterations = 1
        }, default);

        // Assert
        capturedContext.Should().NotBeNull();
        capturedContext!.CurrentCandidate.CandidateId.Should().Be(seedId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

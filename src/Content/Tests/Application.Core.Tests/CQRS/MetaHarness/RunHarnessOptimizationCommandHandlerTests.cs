using Application.AI.Common.Exceptions;
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
/// Unit tests for <see cref="RunHarnessOptimizationCommandHandler"/>.
/// All external collaborators are mocked. Filesystem I/O uses a temp directory per test.
/// </summary>
public sealed class RunHarnessOptimizationCommandHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IHarnessProposer> _proposer;
    private readonly Mock<IEvaluationService> _evaluator;
    private readonly Mock<IHarnessCandidateRepository> _repository;
    private readonly Mock<ISnapshotBuilder> _snapshotBuilder;
    private readonly Mock<IOptionsMonitor<MetaHarnessConfig>> _configMonitor;
    private readonly Mock<ILogger<RunHarnessOptimizationCommandHandler>> _logger;
    private MetaHarnessConfig _cfg;

    public RunHarnessOptimizationCommandHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _proposer = new Mock<IHarnessProposer>();
        _evaluator = new Mock<IEvaluationService>();
        _repository = new Mock<IHarnessCandidateRepository>();
        _snapshotBuilder = new Mock<ISnapshotBuilder>();
        _configMonitor = new Mock<IOptionsMonitor<MetaHarnessConfig>>();
        _logger = new Mock<ILogger<RunHarnessOptimizationCommandHandler>>();

        _cfg = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            MaxIterations = 3,
            EvalTasksPath = Path.Combine(_tempDir, "eval-tasks"),
            ScoreImprovementThreshold = 0.01,
            MaxRunsToKeep = 0,
        };
        _configMonitor.Setup(x => x.CurrentValue).Returns(() => _cfg);
    }

    private RunHarnessOptimizationCommandHandler BuildHandler() =>
        new(_proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _configMonitor.Object, _logger.Object);

    private static RunHarnessOptimizationCommand BuildCommand(
        Guid? runId = null, int? maxIterations = null) =>
        new()
        {
            OptimizationRunId = runId ?? Guid.NewGuid(),
            MaxIterations = maxIterations,
        };

    private HarnessCandidate BuildCandidate(Guid runId, int iteration = 0,
        HarnessCandidateStatus status = HarnessCandidateStatus.Proposed,
        double? score = null, long? cost = null) =>
        new()
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = runId,
            Iteration = iteration,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = status,
            BestScore = score,
            TokenCost = cost,
            Snapshot = BuildSnapshot(),
        };

    private static HarnessSnapshot BuildSnapshot(string? skillContent = null) =>
        new()
        {
            SkillFileSnapshots = skillContent is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["SKILL.md"] = skillContent },
            SystemPromptSnapshot = "system prompt",
            ConfigSnapshot = new Dictionary<string, string>(),
            SnapshotManifest = [],
        };

    private static HarnessProposal BuildProposal(
        IReadOnlyDictionary<string, string>? skills = null) =>
        new()
        {
            ProposedSkillChanges = skills ?? new Dictionary<string, string>(),
            ProposedConfigChanges = new Dictionary<string, string>(),
            ProposedSystemPromptChange = null,
            Reasoning = "test reasoning",
        };

    private void CreateEvalTaskFile(string taskId = "task-1")
    {
        Directory.CreateDirectory(_cfg.EvalTasksPath);
        var json = JsonSerializer.Serialize(new
        {
            TaskId = taskId,
            Description = "desc",
            InputPrompt = "prompt",
            Tags = Array.Empty<string>(),
        });
        File.WriteAllText(Path.Combine(_cfg.EvalTasksPath, $"{taskId}.json"), json);
    }

    private void SetupSeedCandidate(Guid runId)
    {
        var seed = BuildCandidate(runId, iteration: 0);
        _snapshotBuilder
            .Setup(x => x.BuildAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshot());
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupBestCandidate(Guid runId, double score = 0.8)
    {
        var best = BuildCandidate(runId, iteration: 1,
            status: HarnessCandidateStatus.Evaluated, score: score, cost: 100);
        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(best);
        _repository
            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { best });
    }

    [Fact]
    public async Task Handle_ExecutesMaxIterations_WhenAllSucceed()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);
        SetupBestCandidate(runId);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);

        // Assert
        Assert.Equal(3, result.IterationCount);
        Assert.Equal(runId, result.OptimizationRunId);
        // Proposer called exactly 3 times (one per iteration)
        _proposer.Verify(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Handle_ProposerParsingFailure_MarksFailedAndContinues()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);
        SetupBestCandidate(runId, score: 0.8);

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        var callCount = 0;
        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HarnessProposalParsingException("bad output");
                return BuildProposal();
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);

        // Assert: all 3 iterations ran (failures count)
        Assert.Equal(3, result.IterationCount);
        // A failed candidate was saved with Failed status
        Assert.Contains(savedCandidates, c => c.Status == HarnessCandidateStatus.Failed);
    }

    [Fact]
    public async Task Handle_EvaluationException_MarksFailedAndContinues()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);
        SetupBestCandidate(runId, score: 0.8);

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());

        var evalCallCount = 0;
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                evalCallCount++;
                if (evalCallCount == 1) throw new InvalidOperationException("eval blew up");
                return new EvaluationResult(Guid.NewGuid(), 0.8, 100, []);
            });

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);

        // Assert: all 3 iterations ran
        Assert.Equal(3, result.IterationCount);
        // A candidate with Failed status and FailureReason was saved
        var failed = savedCandidates.FirstOrDefault(c => c.Status == HarnessCandidateStatus.Failed);
        Assert.NotNull(failed);
        Assert.NotNull(failed.FailureReason);
    }

    [Fact]
    public async Task Handle_FailuresCountAsIterations_NotSkipped()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);

        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate?)null);
        _repository
            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HarnessProposalParsingException("bad"));

        var handler = BuildHandler();

        // Act — maxIterations = 3, all fail
        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);

        // Assert: proposer called 3 times, not fewer
        _proposer.Verify(
            x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        Assert.Equal(3, result.IterationCount);
    }

    [Fact]
    public async Task Handle_ScoreBelowThreshold_DoesNotUpdateBest()
    {
        // Arrange: threshold=0.1, iter1=0.5, iter2=0.505 (improvement < threshold)
        _cfg.ScoreImprovementThreshold = 0.1;

        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        var iter = 0;
        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                iter++;
                var score = iter == 1 ? 0.5 : 0.505;
                return new EvaluationResult(Guid.NewGuid(), score, 100, []);
            });

        Guid? capturedBestId = null;
        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // Return the first evaluated candidate as the repository's best
                var first = savedCandidates.FirstOrDefault(c =>
                    c.Status == HarnessCandidateStatus.Evaluated && c.BestScore == 0.5);
                return first;
            });
        _repository
            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var handler = BuildHandler();

        // Act
        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);

        // Assert: run_manifest.json bestCandidateId points to the iter-1 candidate
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "run_manifest.json"));
        var manifest = JsonDocument.Parse(manifestJson).RootElement;
        var manifestBest = manifest.TryGetProperty("bestCandidateId", out var best)
            ? best.GetString()
            : null;

        // The first evaluated candidate with score 0.5 should be the best
        var firstEvaluated = savedCandidates.FirstOrDefault(c =>
            c.Status == HarnessCandidateStatus.Evaluated && c.BestScore == 0.5);
        Assert.NotNull(firstEvaluated);
        Assert.Equal(firstEvaluated.CandidateId.ToString(), manifestBest);
    }

    [Fact]
    public async Task Handle_TieOnPassRate_PicksLowerTokenCostCandidate()
    {
        // Arrange: both iterations return same pass rate, iter2 has lower token cost
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        var iter = 0;
        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
            {
                iter++;
                var cost = iter == 1 ? 200L : 100L; // iter2 has lower cost
                return new EvaluationResult(c.CandidateId, 0.8, cost, []);
            });
        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate?)null);
        _repository
            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var handler = BuildHandler();

        // Act
        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);

        // Assert: run_manifest bestCandidateId == iter-2 candidate (lower cost wins the tie)
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "run_manifest.json"));
        var manifest = JsonDocument.Parse(manifestJson).RootElement;
        var manifestBest = manifest.GetProperty("bestCandidateId").GetString();

        var iter2Candidate = savedCandidates
            .Where(c => c.Status == HarnessCandidateStatus.Evaluated && c.TokenCost == 100)
            .LastOrDefault();
        Assert.NotNull(iter2Candidate);
        Assert.Equal(iter2Candidate.CandidateId.ToString(), manifestBest);
    }

    [Fact]
    public async Task Handle_TieOnBoth_PicksEarlierIterationCandidate()
    {
        // Arrange: both iterations return same pass rate and same token cost
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);

        var savedCandidates = new List<HarnessCandidate>();
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
            .Returns(Task.CompletedTask);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));
        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate?)null);
        _repository
            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var handler = BuildHandler();

        // Act
        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);

        // Assert: run_manifest bestCandidateId == iter-1 candidate (earlier iteration wins)
        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "run_manifest.json"));
        var manifest = JsonDocument.Parse(manifestJson).RootElement;
        var manifestBest = manifest.GetProperty("bestCandidateId").GetString();

        var iter1Candidate = savedCandidates
            .Where(c => c.Status == HarnessCandidateStatus.Evaluated && c.Iteration == 1)
            .FirstOrDefault();
        Assert.NotNull(iter1Candidate);
        Assert.Equal(iter1Candidate.CandidateId.ToString(), manifestBest);
    }

    [Fact]
    public async Task Handle_ResumesFromManifest_SkipsAlreadyCompletedIterations()
    {
        // Arrange: pre-write a manifest indicating iteration 2 is already done
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();

        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        Directory.CreateDirectory(runDir);
        var existingManifest = new
        {
            optimizationRunId = runId.ToString(),
            lastCompletedIteration = 2,
            bestCandidateId = (string?)null,
            startedAt = DateTimeOffset.UtcNow.ToString("O"),
            writeCompleted = true,
        };
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "run_manifest.json"),
            JsonSerializer.Serialize(existingManifest));

        SetupSeedCandidate(runId);
        SetupBestCandidate(runId, score: 0.8);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);

        // Assert: proposer called only once (iteration 3 only)
        _proposer.Verify(
            x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(1, result.IterationCount);
    }

    [Fact]
    public async Task Handle_WritesRunManifestAfterEachIteration()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);
        SetupBestCandidate(runId, score: 0.8);

        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
        var manifestUpdates = new List<int>();

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
            {
                // Record manifest state after each evaluation (manifest is written after evaluate)
                return new EvaluationResult(c.CandidateId, 0.8, 100, []);
            });

        var handler = BuildHandler();

        // Act
        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);

        // Assert: run_manifest.json exists and has lastCompletedIteration == 2
        var manifestPath = Path.Combine(runDir, "run_manifest.json");
        Assert.True(File.Exists(manifestPath));
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)).RootElement;
        Assert.Equal(2, json.GetProperty("lastCompletedIteration").GetInt32());
        Assert.True(json.GetProperty("writeCompleted").GetBoolean());
    }

    [Fact]
    public async Task Handle_WritesProposedChangesToOutputDir_AtEnd()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);

        var skillContent = "# Best SKILL.md content";
        var bestCandidate = BuildCandidate(runId, 1, HarnessCandidateStatus.Evaluated, 0.9, 100);
        bestCandidate = bestCandidate with
        {
            Snapshot = BuildSnapshot(skillContent),
        };

        _repository
            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bestCandidate);
        _repository
            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { bestCandidate });

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.9, 100, []));

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(BuildCommand(runId, maxIterations: 1), default);

        // Assert: _proposed/ dir exists with the best candidate's skill file
        var proposedDir = Path.Combine(_tempDir, "optimizations", runId.ToString(), "_proposed");
        Assert.True(Directory.Exists(proposedDir));
        Assert.Equal(proposedDir, result.ProposedChangesPath);
        Assert.True(File.Exists(Path.Combine(proposedDir, "SKILL.md")));
        Assert.Equal(skillContent, await File.ReadAllTextAsync(Path.Combine(proposedDir, "SKILL.md")));
    }

    [Fact]
    public async Task Handle_CancellationRequested_StopsCleanlyBetweenIterations()
    {
        // Arrange
        var runId = Guid.NewGuid();
        CreateEvalTaskFile();
        SetupSeedCandidate(runId);

        using var cts = new CancellationTokenSource();
        var callCount = 0;
        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) cts.Cancel(); // cancel after first proposal
                return BuildProposal();
            });
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act + Assert: OperationCanceledException propagates
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.Handle(BuildCommand(runId, maxIterations: 3), cts.Token));
    }

    [Fact]
    public async Task Handle_RetentionPolicy_DeletesOldestRunsWhenExceedsMaxRunsToKeep()
    {
        // Arrange: create 3 old run directories, MaxRunsToKeep=2
        _cfg.MaxRunsToKeep = 2;

        var runId = Guid.NewGuid();
        var optimizationsDir = Path.Combine(_tempDir, "optimizations");
        Directory.CreateDirectory(optimizationsDir);

        // Create 3 pre-existing runs (oldest first)
        var oldRun1 = Path.Combine(optimizationsDir, Guid.NewGuid().ToString());
        var oldRun2 = Path.Combine(optimizationsDir, Guid.NewGuid().ToString());
        var oldRun3 = Path.Combine(optimizationsDir, Guid.NewGuid().ToString());
        Directory.CreateDirectory(oldRun1);
        await Task.Delay(10); // ensure distinct creation times
        Directory.CreateDirectory(oldRun2);
        await Task.Delay(10);
        Directory.CreateDirectory(oldRun3);

        CreateEvalTaskFile();
        SetupSeedCandidate(runId);
        SetupBestCandidate(runId);

        _proposer
            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildProposal());
        _evaluator
            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
                new EvaluationResult(c.CandidateId, 0.8, 100, []));

        var handler = BuildHandler();

        // Act
        await handler.Handle(BuildCommand(runId, maxIterations: 1), default);

        // Assert: oldest runs deleted, newer ones kept
        // MaxRunsToKeep=2 means at most 1 old run + current run = 2 total
        Assert.False(Directory.Exists(oldRun1), "Oldest run should have been deleted");
        Assert.False(Directory.Exists(oldRun2), "Second-oldest run should have been deleted");
        Assert.True(Directory.Exists(oldRun3), "Most recent old run should be kept");
    }

    [Fact]
    public async Task Handle_NoEvalTasks_ReturnsZeroIterations()
    {
        // Arrange: eval tasks directory is empty / missing
        var runId = Guid.NewGuid();
        // Do NOT create eval task files

        var handler = BuildHandler();

        // Act
        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);

        // Assert: returns immediately with 0 iterations, proposer never called
        Assert.Equal(0, result.IterationCount);
        Assert.Equal(runId, result.OptimizationRunId);
        Assert.Null(result.BestCandidateId);
        _proposer.Verify(
            x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}

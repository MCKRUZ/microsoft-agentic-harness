using System.Collections.Immutable;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.Workflows.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Agents.AI.Workflows;
using Moq;
using Xunit;

namespace Application.Core.Tests.Workflows.MetaHarness;

/// <summary>
/// Regression tests for <see cref="ScoreCandidateExecutor"/> guarding the solution-review
/// fix (finding 24): the executor must read the prior best <em>before</em> persisting the
/// candidate. Saving first pollutes <see cref="IHarnessCandidateRepository.GetBestAsync"/>'s
/// pool with the just-evaluated candidate, which made <see cref="IterationOutcome.IsImprovement"/>
/// unconditionally <c>false</c> for every iteration.
/// </summary>
public sealed class ScoreCandidateExecutorSolutionReviewFixTests
{
    private readonly Mock<IWorkflowContext> _workflowContext = new();

    [Fact]
    public async Task HandleAsync_CandidateBeatsPriorBest_ReportsImprovement()
    {
        var runId = Guid.NewGuid();
        var repository = new FakeHarnessCandidateRepository();

        // Prior best already persisted with a lower score.
        await repository.SaveAsync(CreateEvaluated(runId, iteration: 0, score: 0.50));

        var candidate = CreateEvaluated(runId, iteration: 1, score: 0.80);
        var executor = new ScoreCandidateExecutor(repository);

        var outcome = await executor.HandleAsync(
            new EvaluationStepOutput(CreateEvaluationResult(candidate), candidate),
            _workflowContext.Object,
            CancellationToken.None);

        // With the save-before-fetch bug, GetBestAsync would return the candidate itself
        // (score 0.80 == 0.80), so IsImprovement would be false. The fix fetches the prior
        // best (0.50) first, so the higher-scoring candidate is correctly an improvement.
        Assert.True(outcome.IsImprovement);
        Assert.Equal(0.80, outcome.Score, precision: 5);
    }

    [Fact]
    public async Task HandleAsync_FirstCandidate_ReportsImprovement()
    {
        var runId = Guid.NewGuid();
        var repository = new FakeHarnessCandidateRepository();

        var candidate = CreateEvaluated(runId, iteration: 0, score: 0.40);
        var executor = new ScoreCandidateExecutor(repository);

        var outcome = await executor.HandleAsync(
            new EvaluationStepOutput(CreateEvaluationResult(candidate), candidate),
            _workflowContext.Object,
            CancellationToken.None);

        // With the bug, the just-saved seed candidate is its own "best", so the
        // currentBest-is-null branch never fires and IsImprovement is false. The fix
        // fetches before saving, so the first candidate has no prior best and improves.
        Assert.True(outcome.IsImprovement);
    }

    [Fact]
    public async Task HandleAsync_CandidateWorseThanPriorBest_ReportsNoImprovement()
    {
        var runId = Guid.NewGuid();
        var repository = new FakeHarnessCandidateRepository();
        await repository.SaveAsync(CreateEvaluated(runId, iteration: 0, score: 0.90));

        var candidate = CreateEvaluated(runId, iteration: 1, score: 0.30);
        var executor = new ScoreCandidateExecutor(repository);

        var outcome = await executor.HandleAsync(
            new EvaluationStepOutput(CreateEvaluationResult(candidate), candidate),
            _workflowContext.Object,
            CancellationToken.None);

        Assert.False(outcome.IsImprovement);
    }

    [Fact]
    public async Task HandleAsync_PersistsCandidate()
    {
        var runId = Guid.NewGuid();
        var repository = new FakeHarnessCandidateRepository();
        var candidate = CreateEvaluated(runId, iteration: 0, score: 0.60);
        var executor = new ScoreCandidateExecutor(repository);

        await executor.HandleAsync(
            new EvaluationStepOutput(CreateEvaluationResult(candidate), candidate),
            _workflowContext.Object,
            CancellationToken.None);

        var stored = await repository.GetAsync(candidate.CandidateId);
        Assert.NotNull(stored);
        Assert.Equal(0.60, stored!.BestScore);
    }

    private static EvaluationResult CreateEvaluationResult(HarnessCandidate candidate) =>
        new(candidate.CandidateId, candidate.BestScore ?? 0.0, candidate.TokenCost ?? 0, []);

    private static HarnessCandidate CreateEvaluated(Guid runId, int iteration, double score) =>
        new()
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = runId,
            Iteration = iteration,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = EmptySnapshot,
            BestScore = score,
            TokenCost = 100,
            Status = HarnessCandidateStatus.Evaluated
        };

    private static HarnessSnapshot EmptySnapshot => new()
    {
        SkillFileSnapshots = ImmutableDictionary<string, string>.Empty,
        SystemPromptSnapshot = string.Empty,
        ConfigSnapshot = ImmutableDictionary<string, string>.Empty,
        SnapshotManifest = []
    };

    /// <summary>
    /// In-memory repository mirroring <c>FileSystemHarnessCandidateRepository</c> selection
    /// semantics: <see cref="GetBestAsync"/> returns the highest-<c>BestScore</c> evaluated
    /// candidate among everything persisted so far. This reproduces the real save-order bug.
    /// </summary>
    private sealed class FakeHarnessCandidateRepository : IHarnessCandidateRepository
    {
        private readonly Dictionary<Guid, HarnessCandidate> _store = new();

        public Task SaveAsync(HarnessCandidate candidate, CancellationToken ct = default)
        {
            _store[candidate.CandidateId] = candidate;
            return Task.CompletedTask;
        }

        public Task<HarnessCandidate?> GetAsync(Guid candidateId, CancellationToken ct = default) =>
            Task.FromResult(_store.GetValueOrDefault(candidateId));

        public Task<IReadOnlyList<HarnessCandidate>> GetLineageAsync(Guid candidateId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HarnessCandidate>>([]);

        public Task<HarnessCandidate?> GetBestAsync(Guid optimizationRunId, CancellationToken ct = default)
        {
            var best = _store.Values
                .Where(c => c.OptimizationRunId == optimizationRunId &&
                            c.Status == HarnessCandidateStatus.Evaluated)
                .OrderByDescending(c => c.BestScore ?? 0.0)
                .ThenBy(c => c.TokenCost ?? long.MaxValue)
                .ThenBy(c => c.Iteration)
                .FirstOrDefault();
            return Task.FromResult(best);
        }

        public Task<IReadOnlyList<HarnessCandidate>> ListAsync(Guid optimizationRunId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HarnessCandidate>>(
                _store.Values.Where(c => c.OptimizationRunId == optimizationRunId).ToList());
    }
}

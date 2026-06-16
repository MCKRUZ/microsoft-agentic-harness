using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.MetaHarness;

/// <summary>
/// MediatR handler for <see cref="RunHarnessOptimizationCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// Orchestrates the full propose-evaluate iteration cycle:
/// <list type="number">
///   <item>Load eval tasks from <see cref="MetaHarnessConfig.EvalTasksPath"/>.</item>
///   <item>Resolve or build the seed candidate.</item>
///   <item>For each iteration: propose → build snapshot → evaluate → score → track best.</item>
///   <item>Write final <c>_proposed/</c> snapshot and <c>summary.md</c>.</item>
/// </list>
/// </para>
/// <para>
/// Recoverable failures (proposer parse errors, evaluation exceptions) are caught per-iteration,
/// recorded as <see cref="HarnessCandidateStatus.Failed"/> candidates, and do not abort the run.
/// <see cref="OperationCanceledException"/> always propagates to the caller.
/// </para>
/// </remarks>
public sealed partial class RunHarnessOptimizationCommandHandler
    : IRequestHandler<RunHarnessOptimizationCommand, OptimizationResult>
{
    private readonly IHarnessProposer _proposer;
    private readonly IEvaluationService _evaluationService;
    private readonly IHarnessCandidateRepository _candidateRepository;
    private readonly ISnapshotBuilder _snapshotBuilder;
    private readonly IRegressionSuiteService _regressionService;
    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly ILogger<RunHarnessOptimizationCommandHandler> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Initializes a new instance of <see cref="RunHarnessOptimizationCommandHandler"/>.</summary>
    public RunHarnessOptimizationCommandHandler(
        IHarnessProposer proposer,
        IEvaluationService evaluationService,
        IHarnessCandidateRepository candidateRepository,
        ISnapshotBuilder snapshotBuilder,
        IRegressionSuiteService regressionService,
        IOptionsMonitor<MetaHarnessConfig> config,
        ILogger<RunHarnessOptimizationCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(evaluationService);
        ArgumentNullException.ThrowIfNull(candidateRepository);
        ArgumentNullException.ThrowIfNull(snapshotBuilder);
        ArgumentNullException.ThrowIfNull(regressionService);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _proposer = proposer;
        _evaluationService = evaluationService;
        _candidateRepository = candidateRepository;
        _snapshotBuilder = snapshotBuilder;
        _regressionService = regressionService;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OptimizationResult> Handle(
        RunHarnessOptimizationCommand command,
        CancellationToken cancellationToken)
    {
        var cfg = _config.CurrentValue;
        var maxIterations = command.MaxIterations ?? cfg.MaxIterations;
        var runDir = Path.Combine(
            cfg.TraceDirectoryRoot, "optimizations", command.OptimizationRunId.ToString());
        Directory.CreateDirectory(runDir);

        // Abort early if no eval tasks — not a crash, just a no-op with a warning
        var evalTasks = await LoadEvalTasksAsync(cfg.EvalTasksPath, cancellationToken);
        if (evalTasks.Count == 0)
        {
            _logger.LogWarning(
                "No eval tasks found at '{Path}'. Optimization run {RunId} completed with 0 iterations.",
                cfg.EvalTasksPath, command.OptimizationRunId);
            return new OptimizationResult
            {
                OptimizationRunId = command.OptimizationRunId,
                BestCandidateId = null,
                BestScore = 0.0,
                IterationCount = 0,
                ProposedChangesPath = string.Empty,
            };
        }

        EnforceRetentionPolicy(cfg.MaxRunsToKeep, cfg.TraceDirectoryRoot, command.OptimizationRunId);

        var manifest = await LoadOrCreateRunManifest(runDir, command.OptimizationRunId);
        var startIteration = manifest.LastCompletedIteration + 1;

        var currentCandidate = await ResolveSeedCandidateAsync(command, cfg, cancellationToken);
        HarnessCandidate? currentBestCandidate = null;
        var priorCandidateIds = new List<Guid>();
        var executedIterations = 0;

        // Pre-loop: load regression suite, learnings, and early-stop counter
        var regressionSuite = await _regressionService.LoadAsync(runDir, cancellationToken);
        var priorLearnings = await ReadLearningsFileAsync(runDir);
        var consecutiveNoImprovement = 0;
        var noImprovementLimit = cfg.ConsecutiveNoImprovementLimit;
        EvaluationResult? previousBestEvalResult = null;
        string? earlyStopReason = null;

        for (var i = startIteration; i <= maxIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            executedIterations++;

            // Step 1: Propose
            HarnessProposal proposal;
            try
            {
                var proposerCtx = new HarnessProposerContext
                {
                    CurrentCandidate = currentCandidate,
                    OptimizationRunDirectoryPath = runDir,
                    PriorCandidateIds = priorCandidateIds.AsReadOnly(),
                    Iteration = i,
                    PriorLearnings = priorLearnings,
                };
                proposal = await _proposer.ProposeAsync(proposerCtx, cancellationToken);
            }
            catch (HarnessProposalParsingException ex)
            {
                var failed = new HarnessCandidate
                {
                    CandidateId = Guid.NewGuid(),
                    OptimizationRunId = command.OptimizationRunId,
                    ParentCandidateId = currentCandidate.CandidateId,
                    Iteration = i,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Snapshot = currentCandidate.Snapshot,
                    Status = HarnessCandidateStatus.Failed,
                    FailureReason = ex.Message,
                };
                await _candidateRepository.SaveAsync(failed, cancellationToken);
                priorCandidateIds.Add(failed.CandidateId);
                _logger.LogWarning(
                    "Iteration {Iteration}: proposer parsing failure — {Message}", i, ex.Message);
                UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
                    command.OptimizationRunId, manifest.StartedAt);
                await AppendLearningsAsync(runDir, BuildFailedLearningsEntry(i, ex.Message));
                priorLearnings = await ReadLearningsFileAsync(runDir);
                consecutiveNoImprovement++;
                if (noImprovementLimit > 0 && consecutiveNoImprovement >= noImprovementLimit)
                {
                    earlyStopReason = "no_improvement";
                    _logger.LogInformation(
                        "Stopping early at iteration {Iteration}: {Count} consecutive with no improvement",
                        i, consecutiveNoImprovement);
                    break;
                }
                continue;
            }

            // Step 2: Create candidate from proposal
            var newSnapshot = BuildSnapshotFromProposal(currentCandidate.Snapshot, proposal);
            var candidate = new HarnessCandidate
            {
                CandidateId = Guid.NewGuid(),
                OptimizationRunId = command.OptimizationRunId,
                ParentCandidateId = currentCandidate.CandidateId,
                Iteration = i,
                CreatedAt = DateTimeOffset.UtcNow,
                Snapshot = newSnapshot,
                Status = HarnessCandidateStatus.Proposed,
            };
            await _candidateRepository.SaveAsync(candidate, cancellationToken);
            WriteSnapshotFiles(runDir, candidate);

            // Step 3: Evaluate
            EvaluationResult evalResult;
            try
            {
                evalResult = await _evaluationService.EvaluateAsync(
                    candidate, evalTasks, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = candidate with
                {
                    Status = HarnessCandidateStatus.Failed,
                    FailureReason = ex.Message,
                };
                await _candidateRepository.SaveAsync(failed, cancellationToken);
                priorCandidateIds.Add(failed.CandidateId);
                _logger.LogWarning(
                    "Iteration {Iteration}: evaluation exception — {Message}", i, ex.Message);
                UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
                    command.OptimizationRunId, manifest.StartedAt);
                await AppendLearningsAsync(runDir, BuildFailedLearningsEntry(i, ex.Message));
                priorLearnings = await ReadLearningsFileAsync(runDir);
                consecutiveNoImprovement++;
                if (noImprovementLimit > 0 && consecutiveNoImprovement >= noImprovementLimit)
                {
                    earlyStopReason = "no_improvement";
                    _logger.LogInformation(
                        "Stopping early at iteration {Iteration}: {Count} consecutive with no improvement",
                        i, consecutiveNoImprovement);
                    break;
                }
                continue;
            }

            // Step 4: Score, regression-gate, and track best
            var evaluated = candidate with
            {
                BestScore = evalResult.PassRate,
                TokenCost = evalResult.TotalTokenCost,
                Status = HarnessCandidateStatus.Evaluated,
            };
            await _candidateRepository.SaveAsync(evaluated, cancellationToken);
            priorCandidateIds.Add(evaluated.CandidateId);

            var acceptedAsNewBest = false;
            if (IsBetter(evaluated, currentBestCandidate, cfg.ScoreImprovementThreshold))
            {
                var regressionCheck = _regressionService.Check(regressionSuite, evalResult);
                if (regressionCheck.Passed)
                {
                    regressionSuite = await _regressionService.PromoteAsync(
                        regressionSuite, evalResult, previousBestEvalResult, runDir, cancellationToken);
                    previousBestEvalResult = evalResult;
                    currentBestCandidate = evaluated;
                    acceptedAsNewBest = true;
                    _logger.LogInformation(
                        "Iteration {Iteration}: accepted as new best (pass rate: {PassRate:P2}, regression gate: {RegressionPassRate:P0})",
                        i, evalResult.PassRate, regressionCheck.PassRate);
                }
                else
                {
                    _logger.LogWarning(
                        "Iteration {Iteration}: IsBetter=true but regression gate FAILED " +
                        "(pass rate: {RegressionPassRate:P0}, failed tasks: [{FailedTasks}])",
                        i, regressionCheck.PassRate,
                        string.Join(", ", regressionCheck.FailedTaskIds));
                }
            }

            // Early-stop tracking
            if (acceptedAsNewBest)
                consecutiveNoImprovement = 0;
            else
                consecutiveNoImprovement++;

            // Step 5: Persist run state and learnings
            UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
                command.OptimizationRunId, manifest.StartedAt);
            currentCandidate = evaluated;

            await AppendLearningsAsync(runDir, BuildLearningsEntry(i, proposal, evalResult, acceptedAsNewBest));
            priorLearnings = await ReadLearningsFileAsync(runDir);

            if (noImprovementLimit > 0 && consecutiveNoImprovement >= noImprovementLimit)
            {
                earlyStopReason = "no_improvement";
                _logger.LogInformation(
                    "Stopping early at iteration {Iteration}: {Count} consecutive with no improvement",
                    i, consecutiveNoImprovement);
                break;
            }
        }

        // Use the regression-gate-validated best, not the repository's ungated PassRate pick.
        // _candidateRepository.GetBestAsync selects purely by PassRate among all Evaluated
        // candidates and has no knowledge of regression-gate outcomes, so it can return a
        // candidate whose regression check FAILED. currentBestCandidate is only advanced inside
        // the passed-gate branch above, so it is the only selection consistent with the run
        // manifest's bestCandidateId and the documented promotion contract. If no candidate ever
        // cleared the gate, this stays null and no _proposed/ snapshot is written — the correct,
        // safe outcome rather than emitting a gate-failing (regressive) artifact.
        var bestCandidate = currentBestCandidate;
        var proposedDir = Path.Combine(runDir, "_proposed");
        WriteProposedSnapshot(proposedDir, bestCandidate);
        await WriteSummaryMarkdownAsync(runDir, command.OptimizationRunId, cancellationToken);

        return new OptimizationResult
        {
            OptimizationRunId = command.OptimizationRunId,
            BestCandidateId = bestCandidate?.CandidateId,
            BestScore = bestCandidate?.BestScore ?? 0.0,
            IterationCount = executedIterations,
            ProposedChangesPath = bestCandidate is not null ? proposedDir : string.Empty,
            EarlyStopReason = earlyStopReason,
        };
    }

    private sealed record RunManifest
    {
        [JsonPropertyName("optimizationRunId")]
        public Guid OptimizationRunId { get; init; }

        [JsonPropertyName("lastCompletedIteration")]
        public int LastCompletedIteration { get; init; }

        [JsonPropertyName("bestCandidateId")]
        public Guid? BestCandidateId { get; init; }

        [JsonPropertyName("startedAt")]
        public DateTimeOffset StartedAt { get; init; }

        [JsonPropertyName("writeCompleted")]
        public bool WriteCompleted { get; init; }
    }
}

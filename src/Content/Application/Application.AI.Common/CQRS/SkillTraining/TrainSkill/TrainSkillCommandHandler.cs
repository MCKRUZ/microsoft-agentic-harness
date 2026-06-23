using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Application.AI.Common.Services.SkillTraining.Schedulers;
using Domain.AI.SkillTraining;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.SkillTraining.TrainSkill;

/// <summary>
/// Orchestrates the full skill-training loop. Chains rollout → reflect → aggregate → select →
/// apply → gate per step, with early-stop on patience and per-step checkpointing.
/// </summary>
/// <remarks>
/// <para>
/// Single-handler design (no MediatR re-entrance for inner stages) keeps the inner loop on
/// the same call stack — easier to reason about, fewer per-step allocations, and trivial
/// to instrument. Each stage is invoked through its interface so unit tests substitute
/// deterministic stubs.
/// </para>
/// <para>
/// Epoch boundary mechanisms (slow update, meta-skill memory) are NOT invoked inside this
/// handler. They are separate CQRS commands the orchestrator dispatches via
/// <see cref="IMediator"/> at end-of-epoch, so they participate in the standard pipeline
/// (validation, audit, telemetry) on equal footing.
/// </para>
/// </remarks>
public sealed class TrainSkillCommandHandler
    : IRequestHandler<TrainSkillCommand, Result<SkillTrainingRunResult>>
{
    private readonly IRolloutRunner _rolloutRunner;
    private readonly IPatchProposer _proposer;
    private readonly IPatchAggregator _aggregator;
    private readonly IEditSelector _selector;
    private readonly PatchApplier _applier;
    private readonly IGateEvaluator _gate;
    private readonly HarnessPatchValidator _fence;
    private readonly ISkillTrainingCheckpointStore _checkpointStore;
    private readonly IMediator _mediator;
    private readonly TimeProvider _time;
    private readonly ILogger<TrainSkillCommandHandler> _logger;
    private readonly IGovernanceAuditService? _audit;

    /// <summary>Initializes a new instance of the <see cref="TrainSkillCommandHandler"/> class.</summary>
    /// <remarks>
    /// <paramref name="audit"/> is optional: when a tamper-evident governance audit service is
    /// registered (the full-harness composition root), fence rejections are written to the hash-chain;
    /// when it is absent (a consumer wiring only the skill-training subsystem), rejections are still
    /// logged via <see cref="ILogger{TCategoryName}"/> and the patch is still blocked. The fence is the
    /// security control; the audit is the record of it.
    /// </remarks>
    public TrainSkillCommandHandler(
        IRolloutRunner rolloutRunner,
        IPatchProposer proposer,
        IPatchAggregator aggregator,
        IEditSelector selector,
        PatchApplier applier,
        IGateEvaluator gate,
        HarnessPatchValidator fence,
        ISkillTrainingCheckpointStore checkpointStore,
        IMediator mediator,
        TimeProvider time,
        ILogger<TrainSkillCommandHandler> logger,
        IGovernanceAuditService? audit = null)
    {
        ArgumentNullException.ThrowIfNull(rolloutRunner);
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _rolloutRunner = rolloutRunner;
        _proposer = proposer;
        _aggregator = aggregator;
        _selector = selector;
        _applier = applier;
        _gate = gate;
        _fence = fence;
        _checkpointStore = checkpointStore;
        _mediator = mediator;
        _time = time;
        _logger = logger;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<SkillTrainingRunResult>> Handle(
        TrainSkillCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var cfg = request.Config;

        // Defensive guards in case the consumer registers this handler without the
        // FluentValidation pipeline behavior. The validator is the primary boundary;
        // these inline checks fail fast on the most common misconfigurations.
        if (cfg.Epochs < 1 || cfg.StepsPerEpoch < 1 || cfg.Patience < 1
            || cfg.LrStart < 1 || cfg.LrMin < 1 || cfg.LrMin > cfg.LrStart)
        {
            return Result<SkillTrainingRunResult>.ValidationFailure(
                ["TrainSkillConfig invariants violated; ensure RequestValidationBehavior is registered."]);
        }

        var scheduler = ResolveScheduler(cfg.LrScheduler);
        var totalSteps = cfg.Epochs * cfg.StepsPerEpoch;

        var currentSkill = request.InitialSkill;
        var bestSkill = currentSkill;
        double currentScore = 0.0;
        double bestScore = 0.0;
        var bestStep = 0;
        var consecutiveRejects = 0;
        var globalStep = 0;
        var steps = new List<SkillTrainingStepRecord>(totalSteps);
        var metaMemory = string.Empty;
        var hasAcceptedAny = false;
        IReadOnlyList<RolloutResult> lastValRollouts = [];
        IReadOnlyList<RolloutResult> priorEpochValRollouts = [];

        for (var epoch = 1; epoch <= cfg.Epochs; epoch++)
        {
            for (var stepInEpoch = 1; stepInEpoch <= cfg.StepsPerEpoch; stepInEpoch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                globalStep++;

                // ── Rollout (train batch) ───────────────────────────────────────
                var trainBatch = new RolloutBatch
                {
                    Split = "train",
                    BatchSize = cfg.TrainBatchSize,
                    Seed = cfg.Seed
                };
                var trainRollouts = await _rolloutRunner
                    .RunAsync(currentSkill, trainBatch, cancellationToken).ConfigureAwait(false);

                // ── Reflect ────────────────────────────────────────────────────
                var input = new ReflectionInput
                {
                    CurrentSkill = currentSkill,
                    Rollouts = trainRollouts,
                    MetaSkillMemory = metaMemory,
                    IncludeSuccesses = true
                };
                Patch proposed;
                try
                {
                    proposed = await _proposer.ProposeAsync(input, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Proposer failed at step {Step}; recording as Reject no-op. Repeated failures may indicate a defective proposer impl.",
                        globalStep);
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed: 0, applied: 0));
                    consecutiveRejects++;
                    if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
                    continue;
                }

                // ── Governance fence (Self-Harness Phase 1) ───────────────────
                // Hard-reject — at intake, below and independent of the gate — any proposed patch whose
                // edits target a harness surface the code-owned registry has not marked editable.
                // Validating the PROPOSED patch (before aggregate/select) keeps the audit trail complete:
                // every frozen-surface attempt is recorded, including ones selection would later drop.
                // Aggregate/select only filter and merge existing edits (preserving Edit.Surface), so
                // they cannot introduce a frozen surface that intake did not already see. Running beneath
                // the gate is the whole point: a frozen-surface edit can never be accepted by improving
                // the score. Today only SkillDocument is editable, so legitimate skill-prose patches pass
                // untouched; this is the lock that must already exist before the edit target is ever
                // widened to system prompt / tools / policies (Phase 2/3).
                var fenceResult = _fence.Validate(proposed);
                if (!fenceResult.IsAllowed)
                {
                    var surfaces = string.Join(", ", fenceResult.Violations.Select(v => v.Surface));
                    _logger.LogWarning(
                        "Harness patch fence rejected step {Step}: {Count} edit(s) target frozen surface(s) [{Surfaces}]. Patch not applied or gated.",
                        globalStep, fenceResult.Violations.Count, surfaces);
                    // The fence — not the audit — is the control: an audit-write failure must not abort
                    // the run or let a frozen-surface patch through. Record the rejection best-effort.
                    try
                    {
                        _audit?.Log(
                            request.SkillId,
                            "skill_training.harness_patch_rejected",
                            $"deny: frozen surface(s) [{surfaces}]");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Governance audit write failed for fence rejection at step {Step}; rejection still enforced.",
                            globalStep);
                    }
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed.Edits.Count, applied: 0));
                    consecutiveRejects++;
                    if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
                    continue;
                }

                // ── Aggregate + select ────────────────────────────────────────
                var aggregated = _aggregator.Aggregate([proposed]);
                var lr = scheduler.GetLearningRate(globalStep - 1, totalSteps, cfg.LrStart, cfg.LrMin);
                var selected = _selector.SelectTopK(aggregated, lr);

                if (selected.Edits.Count == 0)
                {
                    // No edits proposed — record as a Reject no-op and continue.
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed.Edits.Count, 0));
                    consecutiveRejects++;
                    if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
                    continue;
                }

                // ── Apply ────────────────────────────────────────────────────
                var applyReport = _applier.Apply(currentSkill, selected);
                if (!applyReport.HasChanges)
                {
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed.Edits.Count, applyReport.AppliedEdits.Count));
                    consecutiveRejects++;
                    if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
                    continue;
                }

                // ── Score candidate on val split ──────────────────────────────
                var valBatch = new RolloutBatch
                {
                    Split = "val",
                    BatchSize = cfg.ValBatchSize,
                    Seed = cfg.Seed
                };
                var valRollouts = await _rolloutRunner
                    .RunAsync(applyReport.NewSkillContent, valBatch, cancellationToken).ConfigureAwait(false);
                if (valRollouts.Count == 0)
                {
                    _logger.LogWarning(
                        "Val split returned 0 rollouts at step {Step}. Check ValBatchSize and IRolloutRunner configuration — every step will Reject.",
                        globalStep);
                }
                lastValRollouts = valRollouts;
                var (candHard, candSoft) = RolloutBatchScorer.Score(valRollouts);

                // ── Gate ─────────────────────────────────────────────────────
                GateResult gateResult;
                if (cfg.GateMode == GateMode.TwoSplitNonRegression)
                {
                    // Held-in non-regression guard: score the candidate on the EXACT items the
                    // current skill was just scored on (pin trainRollouts' ids), then compare the two
                    // means. Pairing by id — the same mechanism SlowUpdate uses for longitudinal
                    // comparison — makes Δ_in a true paired delta instead of trusting the runner to
                    // re-sample an identical batch. The current skill's held-in score reuses this
                    // step's trainRollouts, so only the candidate needs a fresh rollout: the one
                    // extra rollout per step this mode costs. (If trainRollouts is empty the id list
                    // is empty and the runner falls back to sampling — handled by the 0-rollout warn.)
                    var heldInItemIds = trainRollouts.Select(r => r.ItemId).ToArray();
                    var candidateTrainBatch = trainBatch with { ItemIds = heldInItemIds };
                    var candidateTrainRollouts = await _rolloutRunner
                        .RunAsync(applyReport.NewSkillContent, candidateTrainBatch, cancellationToken).ConfigureAwait(false);
                    if (candidateTrainRollouts.Count == 0)
                    {
                        _logger.LogWarning(
                            "Candidate train split returned 0 rollouts at step {Step}. With GateMode=TwoSplitNonRegression this scores the candidate's held-in performance as 0 and will Reject. Check TrainBatchSize and IRolloutRunner configuration.",
                            globalStep);
                    }
                    var (candHardIn, candSoftIn) = RolloutBatchScorer.Score(candidateTrainRollouts);
                    var (curHardIn, curSoftIn) = RolloutBatchScorer.Score(trainRollouts);
                    // Project held-in current the same way the gate projects everything, so the
                    // delta is computed in a single consistent metric space.
                    var currentHeldInScore = _gate.SelectGateScore(
                        curHardIn, curSoftIn, cfg.GateMetric, cfg.MixedWeight);

                    gateResult = _gate.EvaluateTwoSplit(new GateEvaluation
                    {
                        CandidateSkill = applyReport.NewSkillContent,
                        CandidateHard = candHard,
                        CandidateSoft = candSoft,
                        CandidateHeldInHard = candHardIn,
                        CandidateHeldInSoft = candSoftIn,
                        CurrentSkill = currentSkill,
                        CurrentScore = currentScore,
                        CurrentHeldInScore = currentHeldInScore,
                        BestSkill = bestSkill,
                        BestScore = bestScore,
                        BestStep = bestStep,
                        GlobalStep = globalStep,
                        Metric = cfg.GateMetric,
                        MixedWeight = cfg.MixedWeight
                    });
                }
                else
                {
                    gateResult = _gate.Evaluate(
                        candidateSkill: applyReport.NewSkillContent,
                        candidateHard: candHard, candidateSoft: candSoft,
                        currentSkill: currentSkill, currentScore: currentScore,
                        bestSkill: bestSkill, bestScore: bestScore, bestStep: bestStep,
                        globalStep: globalStep,
                        metric: cfg.GateMetric, mixedWeight: cfg.MixedWeight);
                }

                steps.Add(NewStepRecord(globalStep, epoch, gateResult.Action,
                    candidateScore: gateResult.CandidateScore,
                    proposed: proposed.Edits.Count, applied: applyReport.AppliedEdits.Count));

                // ── State + checkpoint ───────────────────────────────────────
                switch (gateResult.Action)
                {
                    case GateAction.AcceptNewBest:
                        currentSkill = gateResult.CurrentSkill;
                        currentScore = gateResult.CurrentScore;
                        bestSkill = gateResult.BestSkill;
                        bestScore = gateResult.BestScore;
                        bestStep = gateResult.BestStep;
                        consecutiveRejects = 0;
                        hasAcceptedAny = true;
                        break;

                    case GateAction.Accept:
                        currentSkill = gateResult.CurrentSkill;
                        currentScore = gateResult.CurrentScore;
                        consecutiveRejects = 0;
                        hasAcceptedAny = true;
                        break;

                    case GateAction.Reject:
                        consecutiveRejects++;
                        break;
                }

                await _checkpointStore.SaveAsync(
                    new SkillTrainingCheckpoint
                    {
                        RunId = request.RunId,
                        SkillId = request.SkillId,
                        Step = globalStep,
                        Epoch = epoch,
                        SkillContent = gateResult.Action == GateAction.Reject ? currentSkill : gateResult.CurrentSkill,
                        SkillHash = ComputeHash(gateResult.Action == GateAction.Reject ? currentSkill : gateResult.CurrentSkill),
                        Score = gateResult.Action == GateAction.Reject ? currentScore : gateResult.CurrentScore,
                        Action = gateResult.Action,
                        MetaSkillMemory = metaMemory,
                        CreatedAt = _time.GetUtcNow()
                    },
                    cancellationToken).ConfigureAwait(false);

                if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
            }

            // ── Epoch boundary mechanisms ─────────────────────────────────────
            if (epoch < cfg.Epochs)
            {
                if (cfg.UseSlowUpdate && priorEpochValRollouts.Count > 0 && lastValRollouts.Count > 0)
                {
                    // Paired comparison uses already-computed val rollouts — no extra LLM cost.
                    // When Seed is fixed in TrainSkillConfig, item ids overlap and SlowUpdate finds them.
                    var slowCmd = new SlowUpdate.SlowUpdateCommand
                    {
                        PriorRollouts = priorEpochValRollouts,
                        CurrentRollouts = lastValRollouts
                    };
                    var slowResult = await _mediator.Send(slowCmd, cancellationToken).ConfigureAwait(false);
                    if (slowResult.IsSuccess && slowResult.Value!.Total > 0)
                    {
                        _logger.LogInformation("Slow update at epoch {Epoch}: {Guidance}",
                            epoch, slowResult.Value.Guidance);
                        metaMemory = string.IsNullOrEmpty(metaMemory)
                            ? slowResult.Value.Guidance
                            : metaMemory + "\n\n" + slowResult.Value.Guidance;
                    }
                }

                if (cfg.UseMetaSkill)
                {
                    metaMemory = await UpdateMetaMemoryAsync(
                        request, currentSkill, currentScore, epoch, metaMemory,
                        cancellationToken).ConfigureAwait(false);
                }

                priorEpochValRollouts = lastValRollouts;
            }
        }

    EarlyStop:
        return Result<SkillTrainingRunResult>.Success(new SkillTrainingRunResult
        {
            RunId = request.RunId,
            BestSkill = bestSkill,
            BestScore = bestScore,
            BestStep = bestStep,
            StepsExecuted = globalStep,
            ConsecutiveRejects = consecutiveRejects,
            HasAcceptedAny = hasAcceptedAny,
            Steps = steps
        });
    }

    private ILrScheduler ResolveScheduler(string key) => key.ToLowerInvariant() switch
    {
        "cosine" => new CosineScheduler(),
        "linear" => new LinearScheduler(),
        "constant" => new ConstantScheduler(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key,
            "unknown LR scheduler; validator should have caught this.")
    };

    private async Task<string> UpdateMetaMemoryAsync(
        TrainSkillCommand request,
        string currentSkill,
        double currentScore,
        int epoch,
        string priorMemory,
        CancellationToken ct)
    {
        var cmd = new MetaSkillUpdate.MetaSkillUpdateCommand
        {
            RunId = request.RunId,
            SkillId = request.SkillId,
            Epoch = epoch,
            CurrentSkill = currentSkill,
            CurrentScore = currentScore,
            PriorMemory = priorMemory
        };
        var result = await _mediator.Send(cmd, ct).ConfigureAwait(false);
        return result.IsSuccess ? result.Value! : priorMemory;
    }

    private static SkillTrainingStepRecord NewStepRecord(
        int step, int epoch, GateAction action, double candidateScore, int proposed, int applied) => new()
    {
        Step = step,
        Epoch = epoch,
        Action = action,
        CandidateScore = candidateScore,
        ProposedEditCount = proposed,
        AppliedEditCount = applied
    };

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}

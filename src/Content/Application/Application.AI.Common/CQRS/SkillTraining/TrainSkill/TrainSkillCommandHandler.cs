using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.ClaimVerification;
using Application.AI.Common.Services.SkillTraining;
using Application.AI.Common.Services.SkillTraining.Schedulers;
using Domain.AI.ClaimVerification;
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
    private readonly IHarnessChangeSuggester _suggester;
    private readonly HarnessChangeSuggestionValidator _suggestionValidator;
    private readonly ConfigSurfaceConstraint _configSurfaceConstraint;
    private readonly ClaimVerificationRunner _claimVerificationRunner;
    private readonly ISkillTrainingCheckpointStore _checkpointStore;
    private readonly IExperienceLibrary _experienceLibrary;
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
        IHarnessChangeSuggester suggester,
        HarnessChangeSuggestionValidator suggestionValidator,
        ConfigSurfaceConstraint configSurfaceConstraint,
        ClaimVerificationRunner claimVerificationRunner,
        ISkillTrainingCheckpointStore checkpointStore,
        IExperienceLibrary experienceLibrary,
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
        ArgumentNullException.ThrowIfNull(suggester);
        ArgumentNullException.ThrowIfNull(suggestionValidator);
        ArgumentNullException.ThrowIfNull(configSurfaceConstraint);
        ArgumentNullException.ThrowIfNull(claimVerificationRunner);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(experienceLibrary);
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
        _suggester = suggester;
        _suggestionValidator = suggestionValidator;
        _configSurfaceConstraint = configSurfaceConstraint;
        _claimVerificationRunner = claimVerificationRunner;
        _checkpointStore = checkpointStore;
        _experienceLibrary = experienceLibrary;
        _mediator = mediator;
        _time = time;
        _logger = logger;
        _audit = audit;
    }

    /// <summary>
    /// Run ids with a run in flight, per checkpoint store. Two runs of one id would both resume from the
    /// same checkpoint and overwrite each other's steps, splicing two outcomes under one id.
    /// </summary>
    private static readonly ConditionalWeakTable<ISkillTrainingCheckpointStore, ConcurrentDictionary<string, byte>> ActiveRuns = new();

    /// <summary>The most lessons from earlier runs a fresh run starts with.</summary>
    private const int SeedLimit = 5;

    /// <inheritdoc />
    public async Task<Result<SkillTrainingRunResult>> Handle(
        TrainSkillCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // In-process only: a checkpoint store shared by several hosts would need its own lease.
        var active = ActiveRuns.GetOrCreateValue(_checkpointStore);
        if (!active.TryAdd(request.RunId, 0))
        {
            return Result<SkillTrainingRunResult>.ValidationFailure(
                [$"RunId '{request.RunId}' already has a run in flight; wait for it to finish or use a new RunId."]);
        }

        try
        {
            return await RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            active.TryRemove(request.RunId, out _);
        }
    }

    private async Task<Result<SkillTrainingRunResult>> RunAsync(
        TrainSkillCommand request,
        CancellationToken cancellationToken)
    {
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

        // Fail fast when a run declares a TargetSurface the code-owned registry has not unlocked.
        // Without this, every step's edits (which target this surface) would be fence-rejected one by
        // one — correct, but opaque. Surfaces stay locked by default; widening is a deliberate,
        // human-owned registration in the composition root, never something the loop can do itself.
        if (!_fence.IsSurfaceEditable(cfg.TargetSurface))
        {
            return Result<SkillTrainingRunResult>.ValidationFailure(
                [$"TargetSurface '{cfg.TargetSurface}' is not editable. The EditableSurfaceRegistry must be widened (in code) to unlock it before a run may target it."]);
        }

        // Tripwire (#841): the skill the run starts from is read by the proposer on step 1. Later skills
        // are checked as patches arrive (below), so this is the only place the starting text is checked.
        var startLeaks = GraderMarkerScanner.Scan([("initial-skill", request.InitialSkill)]);
        if (startLeaks.Count > 0)
        {
            _logger.LogError(
                "Refusing skill-training run {SkillId}: the initial skill carries grader-verdict marker(s) [{Findings}]",
                request.SkillId, GraderMarkerScanner.Describe(startLeaks));
            SafeAudit(request.SkillId, "skill_training.evidence_leak",
                "deny: grader-verdict marker(s) in the initial skill", 0);
            return Result<SkillTrainingRunResult>.Fail(GraderMarkerScanner.EvidenceLeakCode);
        }

        var scheduler = ResolveScheduler(cfg.LrScheduler);
        var totalSteps = cfg.Epochs * cfg.StepsPerEpoch;

        // Resume (#842): an earlier attempt at this run may have left checkpoints. Continue from the last
        // one rather than starting over, but only when they came from this skill, this starting text and
        // this configuration: a reused RunId with different inputs would otherwise splice two runs.
        var fingerprint = ComputeRunFingerprint(request);
        var (resume, refusal) = await LoadResumeStateAsync(request, cfg, fingerprint, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
            return refusal;

        var currentSkill = resume?.CurrentSkill ?? request.InitialSkill;
        var bestSkill = resume?.BestSkill ?? currentSkill;
        var currentScore = resume?.CurrentScore ?? 0.0;
        var bestScore = resume?.BestScore ?? 0.0;
        var bestStep = resume?.BestStep ?? 0;
        var consecutiveRejects = resume?.ConsecutiveRejects ?? 0;
        var globalStep = resume?.GlobalStep ?? 0;
        var steps = new List<SkillTrainingStepRecord>(totalSteps);
        steps.AddRange(resume?.Steps ?? []);
        var experience = resume?.Experience ?? await SeedExperienceAsync(request, cfg, cancellationToken).ConfigureAwait(false);
        var hasAcceptedAny = resume?.HasAcceptedAny ?? false;
        IReadOnlyList<RolloutResult> lastValRollouts = [];
        IReadOnlyList<RolloutResult> priorEpochValRollouts = [];

        if (resume is not null)
        {
            _logger.LogInformation(
                "Resuming skill-training run {RunId} after step {Step} ({Rejects} consecutive reject(s))",
                request.RunId, resume.GlobalStep, resume.ConsecutiveRejects);

            if (resume.NeedsEpochBoundary)
            {
                // The earlier attempt stopped between an epoch's last step and its boundary work. Do that
                // work now (without the slow update, whose validation rollouts are gone) and record it.
                experience = await RunEpochBoundaryAsync(
                    request, cfg, steps[^1].Epoch, currentSkill, experience, [], [], steps,
                    cancellationToken).ConfigureAwait(false);
                await SaveCheckpointAsync(
                    request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // The next step is one past the last one run. Whole epochs before it are skipped; a resume that
        // lands mid-epoch finishes that epoch's remaining steps first. A run that had already used up its
        // patience starts past the last epoch, so no loop runs and it goes straight to its result.
        var patienceExhausted = resume is not null && consecutiveRejects >= cfg.Patience;
        var startEpoch = patienceExhausted ? cfg.Epochs + 1 : (globalStep / cfg.StepsPerEpoch) + 1;
        var startStepInEpoch = (globalStep % cfg.StepsPerEpoch) + 1;

        for (var epoch = startEpoch; epoch <= cfg.Epochs; epoch++)
        {
            for (var stepInEpoch = epoch == startEpoch ? startStepInEpoch : 1; stepInEpoch <= cfg.StepsPerEpoch; stepInEpoch++)
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
                // Tripwire (#841): the proposer must never read the grader's own output. The view carries
                // no score, so a hit means a verdict arrived another way (a runner that wrote its report
                // into a trajectory). A rollout that carries one is left out of what the proposer reads
                // (the gate below still scores it), so tool output that contains the markers can remove
                // its own rollout from the proposer's view but cannot abort the run. If nothing clean
                // is left, or the cross-epoch memory carries one, the read path is wrong: refuse.
                var screened = GraderMarkerScanner.Screen(trainRollouts.Select(ProposerRolloutView.From).ToList());
                var memoryText = ExperienceRenderer.Render(experience);
                var memoryLeaks = GraderMarkerScanner.Scan([("meta-skill-memory", memoryText)]);
                if (screened.AnyExcluded)
                {
                    _logger.LogError(
                        "Step {Step}: {Count} grader-verdict marker(s) left rollout(s) out of the proposer's evidence [{Findings}]",
                        globalStep, screened.Findings.Count, GraderMarkerScanner.Describe(screened.Findings));
                    SafeAudit(request.SkillId, "skill_training.evidence_leak",
                        $"quarantine: {screened.Findings.Count} marker(s) in rollout trajectories", globalStep);
                }

                if (memoryLeaks.Count > 0 || (screened.AnyExcluded && screened.Clean.Count == 0))
                {
                    _logger.LogError(
                        "Refusing skill-training run {SkillId} at step {Step}: no evidence is safe to show the proposer [{Findings}]",
                        request.SkillId, globalStep, GraderMarkerScanner.Describe([.. memoryLeaks, .. screened.Findings]));
                    SafeAudit(request.SkillId, "skill_training.evidence_leak",
                        "deny: no evidence safe to show the proposer", globalStep);
                    return Result<SkillTrainingRunResult>.Fail(GraderMarkerScanner.EvidenceLeakCode);
                }

                var input = new ReflectionInput
                {
                    CurrentSkill = currentSkill,
                    Rollouts = screened.Clean,
                    MetaSkillMemory = memoryText,
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
                    await SaveCheckpointAsync(
                        request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: false,
                        cancellationToken).ConfigureAwait(false);
                    consecutiveRejects++;
                    if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
                    continue;
                }

                // ── Grader-verdict marker in a proposed edit (#841) ───────────
                // An edit that writes a result into the skill would carry it into every later step and
                // any resumed run. Reject the patch, like a fence violation, so the skill itself can
                // never hold a marker (and the proposer cannot lock a run out by writing one).
                var editLeaks = GraderMarkerScanner.Scan(
                    proposed.Edits.Select((e, i) => ($"edit:{i}", (string?)e.Content)));
                if (editLeaks.Count > 0)
                {
                    _logger.LogWarning(
                        "Step {Step}: proposed patch rejected, its edits carry grader-verdict marker(s) [{Findings}]",
                        globalStep, GraderMarkerScanner.Describe(editLeaks));
                    SafeAudit(request.SkillId, "skill_training.evidence_leak",
                        $"deny: {editLeaks.Count} marker(s) in proposed edits", globalStep);
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed.Edits.Count, applied: 0));
                    await SaveCheckpointAsync(
                        request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: false,
                        cancellationToken).ConfigureAwait(false);
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
                    // The fence — not the audit — is the control: SafeAudit swallows write failures so a
                    // frozen-surface patch can never slip through on a broken audit sink.
                    SafeAudit(request.SkillId, "skill_training.harness_patch_rejected",
                        $"deny: frozen surface(s) [{surfaces}]", globalStep);
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed.Edits.Count, applied: 0));
                    await SaveCheckpointAsync(
                        request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: false,
                        cancellationToken).ConfigureAwait(false);
                    consecutiveRejects++;
                    if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
                    continue;
                }

                // ── Run-scope guard (Self-Harness Phase 2) ────────────────────
                // A run optimizes exactly one declared surface (cfg.TargetSurface). The fence above
                // already guaranteed every edit targets an *editable* surface; this narrows further to
                // the run's *declared* surface, so a failure-recovery run cannot also rewrite the skill
                // body even though both may be unlocked. All-or-nothing, like the fence — and below the
                // gate, so an out-of-scope edit can never be score-accepted.
                var outOfScope = proposed.Edits.Where(e => e.Surface != cfg.TargetSurface).ToArray();
                if (outOfScope.Length > 0)
                {
                    var surfaces = string.Join(", ", outOfScope.Select(e => e.Surface).Distinct());
                    _logger.LogWarning(
                        "Run-scope guard rejected step {Step}: {Count} edit(s) target surface(s) [{Surfaces}] outside the run's TargetSurface '{Target}'. Patch not applied or gated.",
                        globalStep, outOfScope.Length, surfaces, cfg.TargetSurface);
                    SafeAudit(request.SkillId, "skill_training.out_of_run_scope",
                        $"deny: edit(s) on [{surfaces}] outside TargetSurface '{cfg.TargetSurface}'", globalStep);
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed.Edits.Count, applied: 0));
                    await SaveCheckpointAsync(
                        request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: false,
                        cancellationToken).ConfigureAwait(false);
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
                    await SaveCheckpointAsync(
                        request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: false,
                        cancellationToken).ConfigureAwait(false);
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
                    await SaveCheckpointAsync(
                        request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: false,
                        cancellationToken).ConfigureAwait(false);
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
                    proposed: proposed.Edits.Count, applied: applyReport.AppliedEdits.Count,
                    trials: BuildTrials(globalStep, applyReport.AppliedEdits, gateResult.Action, gateResult.CandidateScore, currentScore)));

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

                // Audit every accepted change to a harness surface (caveat: audit everything the loop
                // alters). Rejections are recorded as step records; an accept mutates the surface, so it
                // is written to the tamper-evident governance trail. Best-effort and isolated — an audit
                // failure must not unwind an already-gated, already-applied accept.
                if (gateResult.Action is GateAction.Accept or GateAction.AcceptNewBest)
                {
                    SafeAudit(request.SkillId, "skill_training.harness_patch_applied",
                        $"accept: surface '{cfg.TargetSurface}', {applyReport.AppliedEdits.Count} edit(s), score {gateResult.CandidateScore:F4}",
                        globalStep);
                }

                await SaveCheckpointAsync(
                    request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: false,
                    cancellationToken).ConfigureAwait(false);

                if (consecutiveRejects >= cfg.Patience) goto EarlyStop;
            }

            // ── Epoch boundary mechanisms ─────────────────────────────────────
            if (epoch < cfg.Epochs)
            {
                experience = await RunEpochBoundaryAsync(
                    request, cfg, epoch, currentSkill, experience,
                    priorEpochValRollouts, lastValRollouts, steps, cancellationToken).ConfigureAwait(false);
                priorEpochValRollouts = lastValRollouts;

                // The memory just changed, after the epoch's last checkpoint was written. Write that
                // checkpoint again, marked done, so a resume from here starts with the updated memory and
                // does not repeat the work.
                await SaveCheckpointAsync(
                    request, fingerprint, steps[^1], currentSkill, currentScore, experience, epochBoundaryDone: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }

    EarlyStop:
        // Suggestion-only harness-change path (Self-Harness Phase 2 Step 2). Off by default; when a run
        // opts in, the loop asks for advisory config-dial suggestions, bounds-checks each against the
        // code-owned constraint, audits both outcomes, and surfaces the survivors. It NEVER mutates live
        // config and runs after the optimization loop, so it cannot influence the gated result above.
        // A resume that finds the run already finished runs no step, so it has no evidence to give the
        // suggester and must not append the same audit rows a second time.
        var ranAnyStep = resume is null || globalStep > resume.GlobalStep;
        IReadOnlyList<HarnessChangeSuggestion> suggestions = ranAnyStep
            ? await EmitHarnessChangeSuggestionsAsync(
                request, cfg, lastValRollouts, globalStep, cancellationToken).ConfigureAwait(false)
            : [];

        return Result<SkillTrainingRunResult>.Success(new SkillTrainingRunResult
        {
            RunId = request.RunId,
            BestSkill = bestSkill,
            BestScore = bestScore,
            BestStep = bestStep,
            StepsExecuted = globalStep,
            ConsecutiveRejects = consecutiveRejects,
            HasAcceptedAny = hasAcceptedAny,
            Steps = steps,
            HarnessChangeSuggestions = suggestions
        });
    }

    /// <summary>
    /// Emits bounded, never-applied harness-change suggestions at end of run when the config opts in.
    /// Returns the suggestions that passed the code-owned <see cref="ConfigSurfaceConstraint"/>; rejects
    /// and a thrown suggester are isolated so the suggestion path can never fail the run.
    /// </summary>
    private async Task<IReadOnlyList<HarnessChangeSuggestion>> EmitHarnessChangeSuggestionsAsync(
        TrainSkillCommand request,
        TrainSkillConfig cfg,
        IReadOnlyList<RolloutResult> rollouts,
        int finalStep,
        CancellationToken cancellationToken)
    {
        if (!cfg.EmitHarnessChangeSuggestions)
        {
            return [];
        }

        IReadOnlyList<HarnessChangeSuggestion> proposed;
        try
        {
            proposed = await _suggester.SuggestAsync(
                new HarnessSuggestionInput { SkillId = request.SkillId, Rollouts = rollouts },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Advisory by design: a faulty suggester must never fail an already-completed run.
            _logger.LogError(ex,
                "Harness-change suggester threw; no suggestions surfaced. The optimization result is unaffected.");
            return [];
        }

        if (proposed is null || proposed.Count == 0)
        {
            return [];
        }

        var accepted = new List<HarnessChangeSuggestion>(proposed.Count);
        foreach (var suggestion in proposed)
        {
            var surfaced = ValidateAndAuditSuggestion(suggestion, request.SkillId, finalStep);
            if (surfaced is not null)
            {
                accepted.Add(surfaced);
            }
        }

        return await AnnotateWithClaimVerificationAsync(accepted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates one suggestion against the config-surface constraint and audits the outcome. Returns the
    /// suggestion to surface — with its value replaced by the scrubbed canonical form — when allowed, or
    /// <see langword="null"/> when rejected.
    /// </summary>
    private HarnessChangeSuggestion? ValidateAndAuditSuggestion(
        HarnessChangeSuggestion suggestion, string skillId, int finalStep)
    {
        var validation = _suggestionValidator.Validate(suggestion);
        if (validation.IsAllowed)
        {
            _logger.LogInformation(
                "Harness-change suggestion surfaced: {Surface}.{Field} = {Value} (advisory only, not applied).",
                suggestion.Surface, suggestion.Field, validation.NormalizedValue);
            // Safe to audit Field (validation passed → it is the allow-listed constant, not raw input)
            // and NormalizedValue (the scrubbed canonical value), never the raw ProposedValue.
            SafeAudit(skillId, "skill_training.harness_change_suggested",
                $"suggest: {suggestion.Surface}.{suggestion.Field}={validation.NormalizedValue}", finalStep);
            // Surface the canonical value too, so consumers of the run result never see raw proposer text.
            return suggestion with { ProposedValue = validation.NormalizedValue ?? suggestion.ProposedValue };
        }

        // Audit rejections too (audit-everything guardrail). The raw, unvalidated proposer Field and value
        // are logged operationally below but kept OUT of the tamper-evident chain — only the enum surface
        // and the stable reason category are recorded there.
        _logger.LogWarning(
            "Harness-change suggestion rejected: {Surface}.{Field} — {Reason}.",
            suggestion.Surface, suggestion.Field, validation.RejectionReason);
        SafeAudit(skillId, "skill_training.harness_change_rejected",
            $"deny: {suggestion.Surface} reason {validation.RejectionReason}", finalStep);
        return null;
    }

    /// <summary>
    /// Checks every accepted suggestion's claimed <see cref="HarnessChangeSuggestion.CurrentValue"/>
    /// against the live config it names (#319's first consumer), in a single batched
    /// <see cref="ClaimVerificationRunner.RunAsync"/> call rather than one round-trip per suggestion
    /// — the runner already dispatches its whole input concurrently, bounded by
    /// <c>ClaimVerificationConfig.MaxParallelVerifiers</c>, so verifying suggestions one at a time
    /// would pay N sequential round-trips for work it is built to parallelize. A claim that survives
    /// verification (held, or genuinely unverifiable/erroring — fail-safe) surfaces its suggestion
    /// unchanged; a claim the artifact contradicts is never dropped, only annotated — the same
    /// "revise, don't delete" rule <see cref="ClaimVerdict"/>'s factories apply to the claim itself,
    /// extended here to the suggestion it backs. Never throws: a faulty or unconfigured verifier
    /// must not fail an already-completed run, matching <see cref="EmitHarnessChangeSuggestionsAsync"/>'s
    /// own tolerance for a faulty suggester.
    /// </summary>
    private async Task<IReadOnlyList<HarnessChangeSuggestion>> AnnotateWithClaimVerificationAsync(
        IReadOnlyList<HarnessChangeSuggestion> suggestions, CancellationToken cancellationToken)
    {
        // Positional, not suggestion-keyed: HarnessChangeSuggestion is a record with no identity
        // field, so two accepted suggestions could compare equal — index correlation is the only
        // reliable way to zip a verdict back to the suggestion that produced its claim.
        var claims = new Claim?[suggestions.Count];
        for (var i = 0; i < suggestions.Count; i++)
        {
            var configPath = _configSurfaceConstraint.ResolveConfigPath(suggestions[i].Surface, suggestions[i].Field);
            // ConfigSurfaceConstraint governs this surface/field (validation already confirmed that)
            // but may have no known live-config path for it. Nothing to verify against.
            claims[i] = configPath is null ? null : BuildCurrentValueClaim(suggestions[i], configPath);
        }

        var toVerify = claims.Where(c => c is not null).Select(c => c!).ToList();
        if (toVerify.Count == 0)
        {
            return suggestions;
        }

        IReadOnlyList<ClaimVerdict> verdicts;
        try
        {
            verdicts = await _claimVerificationRunner.RunAsync(toVerify, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Claim verification threw while checking harness-change suggestions' current values; surfacing all suggestions unannotated.");
            return suggestions;
        }

        var result = new List<HarnessChangeSuggestion>(suggestions.Count);
        var verdictIndex = 0;
        for (var i = 0; i < suggestions.Count; i++)
        {
            // verdicts can be SHORTER than toVerify — ClaimVerificationRunner.MaxClaims caps the
            // batch and produces no verdict at all for a claim beyond it (logged there, not an
            // error here). Any claim run out of verdicts is left unannotated, exactly like a claim
            // this loop never built in the first place.
            result.Add(claims[i] is null || verdictIndex >= verdicts.Count
                ? suggestions[i]
                : AnnotateIfContradicted(suggestions[i], verdicts[verdictIndex++]));
        }

        return result;
    }

    private static Claim BuildCurrentValueClaim(HarnessChangeSuggestion suggestion, string configPath) => new()
    {
        Text = $"The current value of {suggestion.Surface}.{suggestion.Field} is {suggestion.CurrentValue}.",
        Location = $"{ClaimLocationScheme.Config}:{configPath}",
        ConsequenceSignals = new ClaimConsequenceSignals
        {
            CausesWrite = false,
            // The suggestion is never applied automatically, but a human's accept/reject decision
            // on it depends on trusting CurrentValue — that dependency is what "gates a decision"
            // means here.
            GatesADecision = true
        }
    };

    private HarnessChangeSuggestion AnnotateIfContradicted(HarnessChangeSuggestion suggestion, ClaimVerdict verdict)
    {
        if (verdict.Outcome is not (ClaimVerificationOutcome.Broken or ClaimVerificationOutcome.LocationNotFound))
        {
            return suggestion;
        }

        _logger.LogWarning(
            "Harness-change suggestion's claimed current value could not be confirmed against live config ({Outcome}): {Explanation}",
            verdict.Outcome, verdict.Explanation);
        // RevisedClaim.Confidence carries the same floor ClaimVerdict.Broken/LocationNotFound apply
        // to the claim itself — surfaced here rather than discarded, so a reader of the annotation
        // sees the same signal the claim-verification subsystem recorded, not a re-derived summary.
        return suggestion with
        {
            Rationale = $"[UNVERIFIED CURRENT VALUE ({verdict.RevisedClaim.Confidence:F1} confidence) — " +
                $"{verdict.Outcome}: {verdict.Explanation}] {suggestion.Rationale}"
        };
    }

    private ILrScheduler ResolveScheduler(string key) => key.ToLowerInvariant() switch
    {
        "cosine" => new CosineScheduler(),
        "linear" => new LinearScheduler(),
        "constant" => new ConstantScheduler(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key,
            "unknown LR scheduler; validator should have caught this.")
    };

    /// <summary>
    /// Writes a governance-audit entry best-effort. The audit is the record, not the control: a failed
    /// write is logged and swallowed so it can never unwind an already-enforced reject or an
    /// already-applied accept. No-op when no <c>IGovernanceAuditService</c> is registered.
    /// </summary>
    private void SafeAudit(string skillId, string action, string decision, int step)
    {
        try
        {
            _audit?.Log(skillId, action, decision);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Governance audit write failed for {Action} at step {Step}; the control was still enforced.",
                action, step);
        }
    }

    private static SkillTrainingStepRecord NewStepRecord(
        int step, int epoch, GateAction action, double candidateScore, int proposed, int applied,
        IReadOnlyList<EditTrial>? trials = null) => new()
    {
        Step = step,
        Epoch = epoch,
        Action = action,
        CandidateScore = candidateScore,
        ProposedEditCount = proposed,
        AppliedEditCount = applied,
        Trials = trials ?? []
    };

    /// <summary>
    /// What the gate showed about each edit that reached it. An edit that went in alongside others is
    /// <see cref="EditVerdict.Confounded"/>: the result cannot be attributed to it. A lone edit is
    /// <see cref="EditVerdict.Helped"/> when accepted, <see cref="EditVerdict.Hurt"/> when the result scored
    /// below the skill it started from, and <see cref="EditVerdict.NoObservedEffect"/> otherwise. Derived
    /// here from the gate's decision, never from a model.
    /// </summary>
    private static IReadOnlyList<EditTrial> BuildTrials(
        int step, IReadOnlyList<Edit> applied, GateAction action, double candidateScore, double scoreBefore)
    {
        if (applied.Count == 0)
            return [];

        var verdict = applied.Count > 1 ? EditVerdict.Confounded
            : action is GateAction.Accept or GateAction.AcceptNewBest ? EditVerdict.Helped
            : candidateScore < scoreBefore - 1e-9 ? EditVerdict.Hurt
            : EditVerdict.NoObservedEffect;

        return [.. applied.Select(e => new EditTrial(step, DescribeEdit(e), verdict))];
    }

    private static string DescribeEdit(Edit edit) => edit.Op switch
    {
        EditOp.Append => $"Append \"{Excerpt(edit.Content)}\"",
        EditOp.InsertAfter => $"Insert after \"{Excerpt(edit.Target)}\": \"{Excerpt(edit.Content)}\"",
        EditOp.Replace => $"Replace \"{Excerpt(edit.Target)}\" with \"{Excerpt(edit.Content)}\"",
        EditOp.Delete => $"Delete \"{Excerpt(edit.Target)}\"",
        _ => edit.Op.ToString()
    };

    private static string Excerpt(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 120 ? collapsed : collapsed[..120] + "...";
    }

    private Task SaveCheckpointAsync(
        TrainSkillCommand request, string fingerprint, SkillTrainingStepRecord record,
        string skill, double score, IReadOnlyList<ExperienceEntry> experience, bool epochBoundaryDone, CancellationToken cancellationToken) =>
        _checkpointStore.SaveAsync(
            new SkillTrainingCheckpoint
            {
                RunId = request.RunId,
                SkillId = request.SkillId,
                Step = record.Step,
                Epoch = record.Epoch,
                SkillContent = skill,
                SkillHash = SkillContentHash.Of(skill),
                Score = score,
                Action = record.Action,
                Experience = experience,
                EditTrials = record.Trials,
                RunFingerprint = fingerprint,
                CandidateScore = record.CandidateScore,
                ProposedEditCount = record.ProposedEditCount,
                AppliedEditCount = record.AppliedEditCount,
                EpochBoundaryDone = epochBoundaryDone,
                CreatedAt = _time.GetUtcNow()
            },
            cancellationToken);

    /// <summary>
    /// What happens between two epochs: the paired slow update over the validation rollouts, then the
    /// meta-skill update that draws the epoch's lesson. Returns the run's experience after both.
    /// </summary>
    private async Task<IReadOnlyList<ExperienceEntry>> RunEpochBoundaryAsync(
        TrainSkillCommand request,
        TrainSkillConfig cfg,
        int epoch,
        string currentSkill,
        IReadOnlyList<ExperienceEntry> experience,
        IReadOnlyList<RolloutResult> priorEpochValRollouts,
        IReadOnlyList<RolloutResult> lastValRollouts,
        IReadOnlyList<SkillTrainingStepRecord> steps,
        CancellationToken cancellationToken)
    {
        var guidance = string.Empty;
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
                guidance = slowResult.Value.Guidance;
            }
        }

        var trials = steps.Where(s => s.Epoch == epoch).SelectMany(s => s.Trials).ToList();

        if (cfg.UseMetaSkill)
        {
            var result = await _mediator.Send(
                new MetaSkillUpdate.MetaSkillUpdateCommand
                {
                    RunId = request.RunId,
                    SkillId = request.SkillId,
                    Domain = request.Domain,
                    Epoch = epoch,
                    CurrentSkill = currentSkill,
                    Trials = trials,
                    PairedGuidance = guidance,
                    PriorEntries = experience
                },
                cancellationToken).ConfigureAwait(false);

            // A refused lesson (it carried grader output) is left out and the run goes on without it.
            return result.IsSuccess ? result.Value!.Entries : experience;
        }

        return guidance.Length == 0 ? experience : [.. experience, GuidanceEntry(request, currentSkill, epoch, guidance)];
    }

    /// <summary>
    /// The lesson for a run that keeps the paired comparison but not the meta-skill update: the comparison's
    /// own note, kept in the run and not stored.
    /// </summary>
    private static ExperienceEntry GuidanceEntry(TrainSkillCommand request, string currentSkill, int epoch, string guidance) => new()
    {
        Cue = $"Changing the '{request.SkillId}' skill ({request.Domain})",
        Lesson = guidance.Trim(),
        Evidence = $"Epoch {epoch}: paired re-run of the validation items.",
        Confidence = 0.5,
        SkillHash = SkillContentHash.Of(currentSkill),
        SkillContent = currentSkill,
        Domain = request.Domain,
        SkillId = request.SkillId,
        RunId = request.RunId,
        Epoch = epoch
    };

    /// <summary>
    /// A fresh run starts with the lessons earlier runs of this skill left behind, when the meta-skill
    /// update is on. Best-effort: a memory that cannot be reached gives an empty start, not a failed run.
    /// </summary>
    private async Task<IReadOnlyList<ExperienceEntry>> SeedExperienceAsync(
        TrainSkillCommand request, TrainSkillConfig cfg, CancellationToken cancellationToken)
    {
        if (!cfg.UseMetaSkill)
            return [];

        try
        {
            return await _experienceLibrary.RecallAsync(
                request.Domain, request.SkillId, request.RunId, beforeEpoch: 1, SeedLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not recall earlier lessons for {SkillId}; starting without them.", request.SkillId);
            return [];
        }
    }

    /// <summary>
    /// Rebuilds the loop state from the checkpoints an earlier attempt at this run left behind. Returns
    /// no state when there are none, and a ready failure when they cannot be resumed: they belong to a
    /// different skill, starting text or configuration, they predate resume, or they carry grader output.
    /// </summary>
    private async Task<(TrainSkillResumeState? State, Result<SkillTrainingRunResult>? Refusal)> LoadResumeStateAsync(
        TrainSkillCommand request, TrainSkillConfig cfg, string fingerprint, CancellationToken cancellationToken)
    {
        var stored = await _checkpointStore.ListAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (stored.Count == 0)
            return (null, null);

        var ordered = stored.OrderBy(c => c.Step).ToList();
        var last = ordered[^1];
        if (string.IsNullOrEmpty(last.RunFingerprint))
        {
            return (null, Result<SkillTrainingRunResult>.ValidationFailure(
                [$"RunId '{request.RunId}' holds checkpoints written before resume was supported, so they cannot be matched to this run; use a new RunId."]));
        }

        if (last.RunFingerprint != fingerprint)
        {
            return (null, Result<SkillTrainingRunResult>.ValidationFailure(
                [$"RunId '{request.RunId}' already holds checkpoints from a different skill, starting text or configuration; use a new RunId to start a new run."]));
        }

        // The best skill is the last one the gate promoted. Taking the highest score instead would pick a
        // later checkpoint on a tie (every reject repeats the current score), reporting the wrong step.
        // The store may have evicted the promoted checkpoint; then its highest-scoring one stands in.
        var promoted = ordered.LastOrDefault(c => c.Action == GateAction.AcceptNewBest);
        var best = promoted;
        if (best is null && ordered.Any(c => c.Action is GateAction.Accept))
            best = await _checkpointStore.GetBestAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        var hasAcceptedAny = best is not null || ordered.Any(c => c.Action is GateAction.Accept or GateAction.AcceptNewBest);

        // A restored skill or memory is read by the rollout runner and the proposer exactly like a live
        // one, so it passes the same tripwire (#841): a store written by another build, or tampered with,
        // must not be a way around it.
        var leaks = GraderMarkerScanner.Scan(
        [
            ("checkpoint:skill", last.SkillContent),
            ("checkpoint:best-skill", best?.SkillContent),
            ("checkpoint:meta-skill-memory", ExperienceRenderer.Render(last.Experience))
        ]);
        leaks = [.. leaks, .. last.Experience.SelectMany(ExperienceLibrary.Scan)];
        if (leaks.Count > 0)
        {
            _logger.LogError(
                "Refusing to resume skill-training run {RunId}: the stored checkpoints carry grader-verdict marker(s) [{Findings}]",
                request.RunId, GraderMarkerScanner.Describe(leaks));
            SafeAudit(request.SkillId, "skill_training.evidence_leak",
                "deny: grader-verdict marker(s) in stored checkpoints", last.Step);
            return (null, Result<SkillTrainingRunResult>.Fail(GraderMarkerScanner.EvidenceLeakCode));
        }

        var trailingRejects = ordered.AsEnumerable().Reverse().TakeWhile(c => c.Action == GateAction.Reject).Count();

        return (new TrainSkillResumeState(
            CurrentSkill: last.SkillContent,
            CurrentScore: last.Score,
            BestSkill: best?.SkillContent ?? request.InitialSkill,
            BestScore: best?.Score ?? 0.0,
            BestStep: best?.Step ?? 0,
            ConsecutiveRejects: trailingRejects,
            GlobalStep: last.Step,
            HasAcceptedAny: hasAcceptedAny,
            Experience: last.Experience,
            NeedsEpochBoundary: last.Step % cfg.StepsPerEpoch == 0
                && last.Epoch < cfg.Epochs
                && !last.EpochBoundaryDone
                && trailingRejects < cfg.Patience,
            Steps: ordered.Select(c => NewStepRecord(
                c.Step, c.Epoch, c.Action, c.CandidateScore, c.ProposedEditCount, c.AppliedEditCount, c.EditTrials)).ToList()), null);
    }

    /// <summary>
    /// A digest of everything that decides what a run does: the skill id, the starting text and the
    /// configuration. Checkpoints written under a different digest are not resumed. The epoch budget and
    /// the patience are left out on purpose: extending a run, or giving it more patience after an early
    /// stop, is the natural reason to resume and changes nothing about the steps already run.
    /// </summary>
    private static string ComputeRunFingerprint(TrainSkillCommand request) =>
        SkillContentHash.Of(string.Join('\n',
            request.SkillId,
            request.Domain,
            SkillContentHash.Of(request.InitialSkill),
            JsonSerializer.Serialize(request.Config with { Epochs = 0, Patience = 0 })));
}

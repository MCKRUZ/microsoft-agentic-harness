using System.Security.Cryptography;
using System.Text;
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

        // Fail fast when a run declares a TargetSurface the code-owned registry has not unlocked.
        // Without this, every step's edits (which target this surface) would be fence-rejected one by
        // one — correct, but opaque. Surfaces stay locked by default; widening is a deliberate,
        // human-owned registration in the composition root, never something the loop can do itself.
        if (!_fence.IsSurfaceEditable(cfg.TargetSurface))
        {
            return Result<SkillTrainingRunResult>.ValidationFailure(
                [$"TargetSurface '{cfg.TargetSurface}' is not editable. The EditableSurfaceRegistry must be widened (in code) to unlock it before a run may target it."]);
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
                    // The fence — not the audit — is the control: SafeAudit swallows write failures so a
                    // frozen-surface patch can never slip through on a broken audit sink.
                    SafeAudit(request.SkillId, "skill_training.harness_patch_rejected",
                        $"deny: frozen surface(s) [{surfaces}]", globalStep);
                    steps.Add(NewStepRecord(globalStep, epoch, GateAction.Reject,
                        candidateScore: 0.0, proposed.Edits.Count, applied: 0));
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
        // Suggestion-only harness-change path (Self-Harness Phase 2 Step 2). Off by default; when a run
        // opts in, the loop asks for advisory config-dial suggestions, bounds-checks each against the
        // code-owned constraint, audits both outcomes, and surfaces the survivors. It NEVER mutates live
        // config and runs after the optimization loop, so it cannot influence the gated result above.
        var suggestions = await EmitHarnessChangeSuggestionsAsync(
            request, cfg, lastValRollouts, globalStep, cancellationToken).ConfigureAwait(false);

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
            result.Add(claims[i] is null
                ? suggestions[i]
                : AnnotateIfContradicted(suggestions[i], verdicts[verdictIndex++]));
        }

        return result;
    }

    private static Claim BuildCurrentValueClaim(HarnessChangeSuggestion suggestion, string configPath) => new()
    {
        Text = $"The current value of {suggestion.Surface}.{suggestion.Field} is {suggestion.CurrentValue}.",
        Location = $"config:{configPath}",
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

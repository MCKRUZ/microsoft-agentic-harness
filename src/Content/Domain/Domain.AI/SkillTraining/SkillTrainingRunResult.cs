namespace Domain.AI.SkillTraining;

/// <summary>
/// Final outcome of a <c>TrainSkillCommand</c>: the best skill found across the run, its
/// score, and the per-step audit trail.
/// </summary>
public sealed record SkillTrainingRunResult
{
    /// <summary>Identifier of this training run.</summary>
    public required string RunId { get; init; }

    /// <summary>The best skill document found across the run.</summary>
    public required string BestSkill { get; init; }

    /// <summary>Score of <see cref="BestSkill"/> in metric space [0, 1].</summary>
    public required double BestScore { get; init; }

    /// <summary>Step at which <see cref="BestSkill"/> was promoted.</summary>
    public required int BestStep { get; init; }

    /// <summary>Total steps executed (may be less than the configured budget if patience triggered).</summary>
    public required int StepsExecuted { get; init; }

    /// <summary>Number of consecutive Reject decisions when the run ended.</summary>
    public required int ConsecutiveRejects { get; init; }

    /// <summary>
    /// True iff at least one step produced <see cref="GateAction.AcceptNewBest"/> or
    /// <see cref="GateAction.Accept"/>. When false, <see cref="BestSkill"/> equals the
    /// initial skill and <see cref="BestScore"/> is 0 — the run made no progress.
    /// </summary>
    public required bool HasAcceptedAny { get; init; }

    /// <summary>Per-step audit trail in chronological order.</summary>
    public IReadOnlyList<SkillTrainingStepRecord> Steps { get; init; } = [];

    /// <summary>
    /// Bounded, never-applied harness-change suggestions surfaced by the run (Self-Harness Phase 2
    /// Step 2). Empty unless the run opted in via <c>TrainSkillConfig.EmitHarnessChangeSuggestions</c>
    /// and a registered <c>IHarnessChangeSuggester</c> proposed at least one suggestion that passed the
    /// code-owned <c>ConfigSurfaceConstraint</c>. These are advisory notes for a human — the loop never
    /// applies them.
    /// </summary>
    public IReadOnlyList<HarnessChangeSuggestion> HarnessChangeSuggestions { get; init; } = [];
}

/// <summary>
/// One step's worth of audit data — captured by the orchestrator after the gate decision.
/// </summary>
public sealed record SkillTrainingStepRecord
{
    /// <summary>Step number (1-based for human-readable audit).</summary>
    public required int Step { get; init; }

    /// <summary>Epoch this step belonged to (1-based).</summary>
    public required int Epoch { get; init; }

    /// <summary>The gate decision for this step.</summary>
    public required GateAction Action { get; init; }

    /// <summary>Candidate's projected score this step.</summary>
    public required double CandidateScore { get; init; }

    /// <summary>Number of edits proposed before selection.</summary>
    public required int ProposedEditCount { get; init; }

    /// <summary>Number of edits actually applied (after selection + apply).</summary>
    public required int AppliedEditCount { get; init; }

    /// <summary>
    /// The edits that were applied and put through the gate this step, with what the gate showed about
    /// each (#845). Empty for a step that ended before the gate (a fence rejection, a proposal with no
    /// edits). The epoch's lesson is drawn from these, so they are kept in the checkpoint: a run resumed
    /// mid-epoch still has the epoch's earlier trials.
    /// </summary>
    public IReadOnlyList<EditTrial> Trials { get; init; } = [];
}

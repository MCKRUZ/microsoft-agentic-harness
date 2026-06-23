namespace Domain.AI.SkillTraining;

/// <summary>
/// Strongly-typed knobs for one training run. Held as a config record (not on the command
/// itself) so it can be loaded from <c>appsettings</c>, serialized into a checkpoint, and
/// passed verbatim into resume operations.
/// </summary>
public sealed record TrainSkillConfig
{
    /// <summary>Number of epochs to run (≥ 1).</summary>
    public int Epochs { get; init; } = 3;

    /// <summary>Steps per epoch (≥ 1).</summary>
    public int StepsPerEpoch { get; init; } = 5;

    /// <summary>LR at step 0 (≥ 1). Edits applied per step is bounded by the scheduler.</summary>
    public int LrStart { get; init; } = 8;

    /// <summary>Floor LR (≥ 1 and ≤ <see cref="LrStart"/>).</summary>
    public int LrMin { get; init; } = 1;

    /// <summary>Scheduler key: <c>"cosine"</c> | <c>"linear"</c> | <c>"constant"</c>.</summary>
    public string LrScheduler { get; init; } = "cosine";

    /// <summary>Train batch size per rollout step.</summary>
    public int TrainBatchSize { get; init; } = 8;

    /// <summary>Val batch size for the gate.</summary>
    public int ValBatchSize { get; init; } = 16;

    /// <summary>Which metric the gate compares on.</summary>
    public GateMetric GateMetric { get; init; } = GateMetric.Hard;

    /// <summary>
    /// Gate acceptance policy. Defaults to <see cref="GateMode.TwoSplitNonRegression"/>, which
    /// requires a candidate to avoid regressing on the held-in (train) split as well as improving
    /// on the held-out (val) split. Set to <see cref="GateMode.StrictImprovementHeldOut"/> for the
    /// original single-split behavior (maximize held-out score only). The two-split mode costs one
    /// extra rollout per step (the candidate scored on the train split).
    /// <para>
    /// Note: this orchestrated loop defaults to the safer two-split mode because it can produce the
    /// held-in scores itself. The lower-level <c>GateCandidateSkillCommand.Mode</c> intentionally
    /// defaults to <see cref="GateMode.StrictImprovementHeldOut"/> instead — its caller must supply
    /// held-in inputs explicitly, so it cannot assume they exist.
    /// </para>
    /// </summary>
    public GateMode GateMode { get; init; } = GateMode.TwoSplitNonRegression;

    /// <summary>Soft weight when <see cref="GateMetric"/> is Mixed; [0, 1].</summary>
    public double MixedWeight { get; init; } = 0.5;

    /// <summary>
    /// Early stop after this many consecutive <see cref="GateAction.Reject"/>s (≥ 1). The
    /// SkillOpt paper's default is 2–4 epochs worth of steps; 6 is a safe baseline.
    /// </summary>
    public int Patience { get; init; } = 6;

    /// <summary>Whether to run the SlowUpdate longitudinal pass at each epoch boundary.</summary>
    public bool UseSlowUpdate { get; init; } = true;

    /// <summary>Whether to maintain a cross-epoch meta-skill memory via <c>IKnowledgeMemory</c>.</summary>
    public bool UseMetaSkill { get; init; } = true;

    /// <summary>Seed for sampling the train/val batches; 0 = nondeterministic.</summary>
    public int Seed { get; init; }

    /// <summary>
    /// The single harness surface this run is permitted to optimize. Defaults to
    /// <see cref="HarnessSurface.SkillDocument"/> — the original, only-editable-today surface.
    /// </summary>
    /// <remarks>
    /// This is the per-run scope, narrower than (and validated against) the code-owned
    /// <c>EditableSurfaceRegistry</c>: the surface must be marked editable by the registry, and the
    /// fence (<c>HarnessPatchValidator</c>) rejects, below the gate, any edit whose surface differs
    /// from this value. A run therefore touches exactly one declared surface — the "get specific about
    /// what it can and cannot alter" guardrail of Self-Harness Phase 2.
    /// </remarks>
    public HarnessSurface TargetSurface { get; init; } = HarnessSurface.SkillDocument;
}

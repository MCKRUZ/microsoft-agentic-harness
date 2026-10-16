namespace Domain.AI.SkillTraining;

/// <summary>
/// A point-in-time snapshot of skill-training state — written after every accepted update
/// so a training run can be resumed or rolled back.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the plan-state-store pattern used by <c>PlanExecutor</c>: each checkpoint is
/// self-contained and addressable by <see cref="RunId"/> + <see cref="Step"/>. Persistence
/// is an Infrastructure concern; this record is the wire-format the Application layer
/// hands to <c>ISkillTrainingCheckpointStore</c>.
/// </para>
/// <para>
/// <see cref="MetaSkillMemory"/> accumulates cross-epoch optimizer guidance and is
/// re-injected as additional context on subsequent reflection steps.
/// </para>
/// </remarks>
public sealed record SkillTrainingCheckpoint
{
    /// <summary>Stable identifier for the training run this checkpoint belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>Skill identifier being trained (matches <c>SkillDefinition.Id</c>).</summary>
    public required string SkillId { get; init; }

    /// <summary>Training step number (monotonically increasing within the run).</summary>
    public required int Step { get; init; }

    /// <summary>Training epoch number (monotonically increasing within the run).</summary>
    public required int Epoch { get; init; }

    /// <summary>The skill document content at this checkpoint.</summary>
    public required string SkillContent { get; init; }

    /// <summary>
    /// Stable hash of <see cref="SkillContent"/> for fast equality checks.
    /// <b>Convention:</b> SHA-256 of the UTF-8-encoded content, lowercase hex with no
    /// separators (e.g. <c>"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"</c>).
    /// Callers must agree on this format or checkpoint dedupe will silently break.
    /// </summary>
    public required string SkillHash { get; init; }

    /// <summary>
    /// Gate metric score recorded when this checkpoint was written.
    /// Allows ranking checkpoints without re-running the gate.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>The gate decision that produced this checkpoint.</summary>
    public required GateAction Action { get; init; }

    /// <summary>Cross-epoch optimizer strategy memory, if any.</summary>
    public string MetaSkillMemory { get; init; } = string.Empty;

    /// <summary>
    /// A digest of the skill id, starting text and configuration the run was started with. A run resumes
    /// from these checkpoints only when its own digest matches, so a reused run id with different inputs
    /// cannot splice two runs together. Empty on a checkpoint written before resume existed.
    /// </summary>
    public string RunFingerprint { get; init; } = string.Empty;

    /// <summary>UTC timestamp when this checkpoint was written.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

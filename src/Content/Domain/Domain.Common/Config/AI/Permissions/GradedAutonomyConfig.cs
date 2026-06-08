namespace Domain.Common.Config.AI.Permissions;

/// <summary>
/// Configuration for graded autonomy (PR-4): per-environment, per-blast-radius,
/// and per-skill rules layered over the tier defaults in
/// <see cref="PermissionsConfig.TierPolicies"/>. Bound from
/// <c>AppConfig:AI:Permissions:GradedAutonomy</c>.
/// </summary>
/// <remarks>
/// <para>
/// Layering order from coarsest to finest (each layer overrides the previous):
/// </para>
/// <list type="number">
///   <item><description>Tier defaults — <see cref="PermissionsConfig.TierPolicies"/>.</description></item>
///   <item><description>Per-environment overrides — <see cref="PerEnvironment"/> keyed by <c>IHostEnvironment.EnvironmentName</c>.</description></item>
///   <item><description>Per-skill overrides — <see cref="PerSkill"/>, more restrictive only (cannot loosen the resolved tier).</description></item>
/// </list>
/// <para>
/// State-changers default to "RequiresApproval" regardless of layer. To opt a
/// specific (skill, blast radius) pair into auto-approval for state changes, add
/// the skill key to <see cref="StateChangerOptIns"/> AND ensure the row's
/// <c>AllowAutoApproveForStateChange</c> is true. Both are required so a single
/// misconfig cannot weaken the safety default.
/// </para>
/// </remarks>
public sealed class GradedAutonomyConfig
{
    /// <summary>
    /// Master toggle. When false (the default), the gate resolver falls back to
    /// the PR-2 behavior — Trivial proposals auto-approve, everything else routes
    /// through approval. When true, the evaluator runs and per-environment,
    /// per-skill, and per-blast-radius overrides take effect.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-environment overrides. Keys are environment names matched against
    /// <c>IHostEnvironment.EnvironmentName</c> (case-insensitive). Values map a
    /// per-environment <see cref="EnvironmentAutonomyConfig"/> that the
    /// evaluator unions over the tier defaults.
    /// </summary>
    /// <remarks>
    /// Typical map:
    /// <code>
    /// PerEnvironment:
    ///   Development: { Permissive defaults }
    ///   Staging:     { Mid defaults }
    ///   Production:  { Strictest defaults }
    /// </code>
    /// Missing environment → no per-environment overlay (tier defaults rule).
    /// </remarks>
    public Dictionary<string, EnvironmentAutonomyConfig> PerEnvironment { get; set; } = new();

    /// <summary>
    /// Per-skill overrides. Key is the skill key (matches <c>SubagentDefinition.SkillKey</c>
    /// or the agent's manifest skill name). Values map to <see cref="SkillAutonomyConfig"/>.
    /// </summary>
    /// <remarks>
    /// Per-skill overrides may only narrow the resolved autonomy (declare a stricter
    /// tier, raise a row to RequiresApproval). The evaluator silently ignores any
    /// per-skill rule that would loosen the resolved tier — and logs a warning so
    /// the misconfig surfaces.
    /// </remarks>
    public Dictionary<string, SkillAutonomyConfig> PerSkill { get; set; } = new();

    /// <summary>
    /// Skill keys that are explicitly opted into auto-approval for state-changing
    /// proposals. Without an entry here, no skill can auto-approve a state change
    /// regardless of any per-blast-radius rule's <c>AllowAutoApproveForStateChange</c>
    /// flag. This is the second half of the dual-key safety check.
    /// </summary>
    public List<string> StateChangerOptIns { get; set; } = [];
}

/// <summary>
/// Per-environment overlay applied on top of the tier defaults.
/// </summary>
public sealed class EnvironmentAutonomyConfig
{
    /// <summary>
    /// Per-blast-radius rules for this environment. Each row maps a
    /// <see cref="Domain.AI.Changes.BlastRadius"/> name
    /// (<c>"Trivial"</c>, <c>"Low"</c>, <c>"Medium"</c>, <c>"High"</c>, <c>"Critical"</c>)
    /// to a decision name (<c>"AutoApprove"</c>, <c>"RequiresApproval"</c>, <c>"Forbidden"</c>).
    /// Missing rows fall back to tier defaults.
    /// </summary>
    public Dictionary<string, BlastRadiusRuleConfig> PerBlastRadius { get; set; } = new();
}

/// <summary>
/// Per-skill overlay applied on top of the environment-resolved tier policy.
/// </summary>
public sealed class SkillAutonomyConfig
{
    /// <summary>
    /// Optional autonomy-tier ceiling for this skill. When set, the evaluator
    /// uses <c>min(resolved tier, this tier)</c> — the skill can only narrow the
    /// resolved tier, never widen it. Valid values: <c>"Restricted"</c>,
    /// <c>"Supervised"</c>, <c>"Autonomous"</c>.
    /// </summary>
    public string? Tier { get; set; }

    /// <summary>
    /// Per-blast-radius overrides for this specific skill. Same shape and rules as
    /// <see cref="EnvironmentAutonomyConfig.PerBlastRadius"/> but only narrows.
    /// </summary>
    public Dictionary<string, BlastRadiusRuleConfig> PerBlastRadius { get; set; } = new();
}

/// <summary>
/// One row in a per-blast-radius rule map. Mirrors
/// <c>Domain.AI.Governance.BlastRadiusAutonomyRule</c> but with string-typed
/// fields for configuration binding. The evaluator parses + validates these at
/// load time.
/// </summary>
public sealed class BlastRadiusRuleConfig
{
    /// <summary>
    /// Decision name. Valid values: <c>"AutoApprove"</c>, <c>"RequiresApproval"</c>,
    /// <c>"Forbidden"</c>. Defaults to <c>"RequiresApproval"</c> — the safe default.
    /// </summary>
    public string Decision { get; set; } = "RequiresApproval";

    /// <summary>
    /// State-change escape hatch. When true, the row permits auto-approve even for
    /// state-changing proposals (still gated by the skill's presence in
    /// <see cref="GradedAutonomyConfig.StateChangerOptIns"/>). Defaults to false.
    /// </summary>
    public bool AllowAutoApproveForStateChange { get; set; }
}

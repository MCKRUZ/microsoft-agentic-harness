namespace Domain.Common.Config.AI.SkillTraining;

/// <summary>
/// Configuration for the governed skill-training implementations: the agentic patch proposer and the
/// rollout runner that stand behind <c>IPatchProposer</c> and <c>IRolloutRunner</c>. Bound from
/// <c>AppConfig:AI:SkillTraining</c>.
/// </summary>
/// <remarks>
/// <code>
/// AppConfig.AI.SkillTraining
/// ├── EvidenceRoot        — Where each step's evidence tree is written for the proposer to read
/// ├── ProposerAgentName   — The agent (AGENT.md) that runs the proposer session
/// ├── MaxProposerTurns    — Turn budget for one proposer session
/// ├── WorkDescription     — What the skill's work is, for the proposer's mission
/// ├── DatasetPath         — The eval dataset the rollout runner draws items from
/// ├── RolloutAgentName    — The agent whose system prompt is replaced by the candidate skill
/// └── MaxRolloutConcurrency — Rollouts run at once within a batch
/// </code>
/// </remarks>
public class SkillTrainingConfig
{
    /// <summary>
    /// Directory the proposer's evidence trees are written under, one folder per run and step. The
    /// proposer's read-only <c>evidence_reader</c> tool is confined to this directory and nothing else.
    /// </summary>
    /// <value>Default: <c>skill-training-evidence</c> (relative to the working directory).</value>
    public string EvidenceRoot { get; set; } = "skill-training-evidence";

    /// <summary>
    /// Name of the agent (an <c>AGENT.md</c> definition) the proposer session runs as. The shipped
    /// <c>skill-optimizer</c> agent is granted <c>evidence_reader</c> and no other tool.
    /// </summary>
    /// <value>Default: <c>skill-optimizer</c>.</value>
    public string ProposerAgentName { get; set; } = "skill-optimizer";

    /// <summary>Maximum agent turns one proposer session may use.</summary>
    /// <value>Default: 12.</value>
    public int MaxProposerTurns { get; set; } = 12;

    /// <summary>
    /// A plain description of the work the skill supports, shown to the proposer. It must describe the
    /// work and not its measurement: words such as benchmark, grader, rubric or score are rejected at
    /// startup, because naming the measurement invites the proposer to write for it (#841).
    /// </summary>
    /// <value>Default: empty (the proposer is told only the skill and the evidence).</value>
    public string WorkDescription { get; set; } = string.Empty;

    /// <summary>
    /// Path to the eval dataset (YAML or JSON, by extension) the rollout runner draws its items from.
    /// Each case selects its split with a <c>split:&lt;name&gt;</c> tag (<c>split:train</c>,
    /// <c>split:val</c>); a case without one is never rolled out. Empty means no runner is configured.
    /// </summary>
    /// <value>Default: empty.</value>
    public string DatasetPath { get; set; } = string.Empty;

    /// <summary>
    /// Name of the agent (an <c>AGENT.md</c> definition) every rollout runs as, with the candidate skill
    /// document in place of its system prompt. Required when <see cref="DatasetPath"/> is set.
    /// </summary>
    /// <value>Default: empty.</value>
    public string RolloutAgentName { get; set; } = string.Empty;

    /// <summary>
    /// How many rollouts of one batch run at the same time. Each is a full agent turn with tool calls,
    /// so the default is deliberately low.
    /// </summary>
    /// <value>Default: 2.</value>
    public int MaxRolloutConcurrency { get; set; } = 2;
}

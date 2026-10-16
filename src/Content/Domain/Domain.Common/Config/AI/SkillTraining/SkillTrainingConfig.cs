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
/// └── WorkDescription     — What the skill's work is, for the proposer's mission
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
}

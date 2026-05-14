namespace Domain.Common.Config.AI.Orchestration;

/// <summary>
/// Configuration for subagent orchestration controlling concurrency limits,
/// per-agent turn caps, and mailbox-based inter-agent communication.
/// Bound from <c>AppConfig:AI:Orchestration:Subagent</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Subagents are spawned by the parent agent for parallelizable work. Each subagent
/// runs independently with its own context window, limited to <see cref="DefaultMaxTurnsPerSubagent"/>
/// turns. Inter-agent messages are exchanged via a file-based mailbox at <see cref="MailboxStoragePath"/>.
/// </para>
/// </remarks>
public class SubagentConfig
{
    /// <summary>
    /// Gets or sets the maximum number of subagents that can execute concurrently.
    /// </summary>
    public int MaxConcurrentSubagents { get; set; } = 3;

    /// <summary>
    /// Gets or sets the default maximum number of turns (LLM round-trips) each subagent
    /// is allowed before being terminated. Individual subagent spawns can override this value.
    /// </summary>
    public int DefaultMaxTurnsPerSubagent { get; set; } = 10;

    /// <summary>
    /// Gets or sets the file system path for the mailbox used for inter-agent message passing.
    /// Relative paths are resolved from the working directory.
    /// </summary>
    public string MailboxStoragePath { get; set; } = ".agent-sessions/mailbox";

    /// <summary>
    /// Maximum depth of nested delegations (supervisor -> agent -> sub-agent).
    /// Prevents infinite delegation chains.
    /// </summary>
    public int MaxDelegationDepth { get; set; } = 3;

    /// <summary>
    /// Filesystem path for delegation record storage (append-only JSONL).
    /// Relative paths resolve from the working directory.
    /// </summary>
    public string DelegationStoragePath { get; set; } = ".agent-sessions/delegations";

    /// <summary>
    /// Maximum time in seconds to wait for a delegated agent to complete.
    /// Expired delegations are cancelled and recorded as failed.
    /// </summary>
    public int DelegationTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of concurrent active delegations across all supervisors.
    /// Additional requests block until a slot is available.
    /// </summary>
    public int MaxConcurrentDelegations { get; set; } = 5;

    /// <summary>
    /// Weights for the capability match scoring algorithm used by the supervisor
    /// to select the best agent for a delegated task.
    /// </summary>
    public CapabilityMatchWeightsConfig CapabilityMatchWeights { get; set; } = new();
}

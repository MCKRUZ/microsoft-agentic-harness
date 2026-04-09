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
}

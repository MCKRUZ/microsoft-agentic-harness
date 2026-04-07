namespace Domain.Common.Config.Infrastructure;

/// <summary>
/// Configuration for state management services.
/// </summary>
/// <remarks>
/// Configures how workflow state is persisted, including JSON checkpointing
/// and markdown file generation for human-readable documentation.
/// <para>
/// <strong>Configuration Example:</strong>
/// <code>
/// {
///   "Infrastructure": {
///     "StateManagement": {
///       "BasePath": "projects",
///       "AgentSkillsPath": "skills",
///       "EnableMarkdownGeneration": true,
///       "EnableJsonCheckpointing": true
///     }
///   }
/// }
/// </code>
/// </para>
/// </remarks>
public class StateManagementConfig
{
    /// <summary>
    /// Gets or sets the base path where workflow state files are stored.
    /// </summary>
    /// <value>
    /// Default: <c>"projects"</c>.
    /// Workflow state files are stored at: <c>{BasePath}/{workflowId}/...</c>
    /// </value>
    public string BasePath { get; set; } = "projects";

    /// <summary>
    /// Gets or sets the path to the agent skills (AGENT.md files) for reading state configurations.
    /// </summary>
    public string AgentSkillsPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to enable markdown file generation alongside JSON checkpointing.
    /// </summary>
    /// <value>
    /// When <c>true</c>, markdown files are generated in addition to JSON checkpoints for
    /// human-readable documentation and git version control. Default: <c>true</c>.
    /// </value>
    public bool EnableMarkdownGeneration { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to enable JSON checkpointing for Agent Framework compatibility.
    /// </summary>
    /// <value>
    /// When <c>true</c>, JSON checkpoints are created for efficient programmatic access and
    /// Agent Framework workflow resume capability. Default: <c>true</c>.
    /// </value>
    public bool EnableJsonCheckpointing { get; set; } = true;
}

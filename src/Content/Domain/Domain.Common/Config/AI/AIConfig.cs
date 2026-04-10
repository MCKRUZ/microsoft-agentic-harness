using Domain.Common.Config.AI.A2A;
using Domain.Common.Config.AI.AIFoundry;
using Domain.Common.Config.AI.ContextManagement;
using Domain.Common.Config.AI.Hooks;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Orchestration;
using Domain.Common.Config.AI.Permissions;

namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for AI services including MCP server/client settings,
/// agent framework, AI Foundry, and model selection.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI
/// ├── AgentFramework    — Provider type and default deployment
/// ├── AIFoundry         — Azure AI Foundry persistent agents
/// ├── MCP               — Server-side MCP configuration (auth, tool assemblies)
/// ├── McpServers        — Client-side MCP server registry (external servers to connect to)
/// ├── A2A               — Agent-to-Agent protocol configuration
/// ├── ContextManagement — Compaction, tool result storage, and budget tracking
/// ├── Permissions       — Permission system for tool and file access approvals
/// ├── Hooks             — Lifecycle hook execution configuration
/// └── Orchestration     — Subagent management and streaming execution
/// </code>
/// </para>
/// </remarks>
public class AIConfig
{
    /// <summary>
    /// Gets or sets the agent framework provider and default deployment settings.
    /// </summary>
    public AgentFrameworkConfig AgentFramework { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure AI Foundry configuration for persistent agents.
    /// </summary>
    public AIFoundryConfig AIFoundry { get; set; } = new();

    /// <summary>
    /// Gets or sets the MCP server-side configuration (when this app is the server).
    /// </summary>
    public McpConfig MCP { get; set; } = new();

    /// <summary>
    /// Gets or sets the MCP client-side server registry (external servers to consume).
    /// </summary>
    public McpServersConfig McpServers { get; set; } = new();

    /// <summary>
    /// Gets or sets the Agent-to-Agent protocol configuration.
    /// </summary>
    public A2AConfig A2A { get; set; } = new();

    /// <summary>
    /// Gets or sets the context management configuration for compaction, tool result
    /// storage, and budget tracking.
    /// </summary>
    public ContextManagementConfig ContextManagement { get; set; } = new();

    /// <summary>
    /// Gets or sets the permission system configuration controlling tool and file
    /// access approvals.
    /// </summary>
    public PermissionsConfig Permissions { get; set; } = new();

    /// <summary>
    /// Gets or sets the hook system configuration for lifecycle callback execution.
    /// </summary>
    public HooksConfig Hooks { get; set; } = new();

    /// <summary>
    /// Gets or sets the orchestration configuration for subagent management and
    /// streaming tool execution.
    /// </summary>
    public OrchestrationConfig Orchestration { get; set; } = new();

    /// <summary>
    /// Gets or sets the filesystem skill discovery configuration.
    /// Controls where SKILL.md files are loaded from.
    /// </summary>
    public SkillsConfig Skills { get; set; } = new();
}

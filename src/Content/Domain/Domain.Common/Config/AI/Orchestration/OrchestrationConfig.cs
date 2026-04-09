namespace Domain.Common.Config.AI.Orchestration;

/// <summary>
/// Root configuration for agent orchestration including subagent management
/// and streaming execution. Bound from <c>AppConfig:AI:Orchestration</c>
/// in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.Orchestration
/// ├── Subagent          — Concurrency limits, turn caps, and mailbox storage
/// └── StreamingExecution — Parallel tool batching and progress reporting
/// </code>
/// </para>
/// </remarks>
public class OrchestrationConfig
{
    /// <summary>
    /// Gets or sets the subagent orchestration configuration controlling concurrency,
    /// turn limits, and inter-agent communication.
    /// </summary>
    public SubagentConfig Subagent { get; set; } = new();

    /// <summary>
    /// Gets or sets the streaming execution configuration for batched parallel
    /// tool execution and progress reporting.
    /// </summary>
    public StreamingExecutionConfig StreamingExecution { get; set; } = new();
}

namespace Application.Common.Models;

/// <summary>
/// Extended run manifest with agentic execution metadata, serialized alongside
/// log files by <c>AgentFileLoggerProvider</c> for post-hoc analysis.
/// </summary>
/// <remarks>
/// Extends <see cref="RunManifest"/> with agent-specific fields: participating agents,
/// tool invocation summaries, turn counts, content safety blocks, and MCP server usage.
/// This enables dashboards and analysis tools to understand agent behavior without parsing logs.
/// </remarks>
public record AgentRunManifest : RunManifest
{
    /// <summary>Gets the agents that participated in this run, with per-agent metrics.</summary>
    public IReadOnlyList<AgentParticipant> Agents { get; init; } = [];

    /// <summary>Gets the summary of tool invocations during the run.</summary>
    public IReadOnlyList<ToolInvocationSummary> ToolInvocations { get; init; } = [];

    /// <summary>Gets the total number of conversation turns across all agents.</summary>
    public int TurnCount { get; init; }

    /// <summary>Gets the number of requests blocked by content safety middleware.</summary>
    public int ContentSafetyBlocks { get; init; }

    /// <summary>Gets the names of MCP servers that were connected during the run.</summary>
    public IReadOnlyList<string> McpServersUsed { get; init; } = [];
}

/// <summary>
/// Represents an agent that participated in an execution run.
/// </summary>
public record AgentParticipant
{
    /// <summary>Gets the agent's unique identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>Gets the parent agent's identifier, if this is a subagent.</summary>
    public string? ParentAgentId { get; init; }

    /// <summary>Gets the total tokens consumed by this agent (input + output).</summary>
    public long TokensUsed { get; init; }

    /// <summary>Gets the number of turns this agent executed.</summary>
    public int TurnCount { get; init; }
}

/// <summary>
/// Aggregated summary of a tool's usage during an execution run.
/// </summary>
public record ToolInvocationSummary
{
    /// <summary>Gets the tool name or key.</summary>
    public required string ToolName { get; init; }

    /// <summary>Gets the number of times this tool was invoked.</summary>
    public int InvocationCount { get; init; }

    /// <summary>Gets the total time spent executing this tool across all invocations.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Gets the number of successful invocations.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Gets the number of failed invocations.</summary>
    public int FailureCount { get; init; }
}

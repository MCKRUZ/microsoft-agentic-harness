using System.Collections.Immutable;
using Domain.AI.Sandbox;

namespace Domain.AI.Planner;

/// <summary>
/// Configuration for a tool execution step. Routes the tool invocation through
/// the appropriate sandbox based on the tool's capability profile.
/// </summary>
public sealed record ToolUseConfig : StepConfiguration
{
    /// <summary>The keyed DI tool name (e.g., <c>"file_system"</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>Input parameters passed to the tool at execution time.</summary>
    public IReadOnlyDictionary<string, object?> InputParameters { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// Optional override for the sandbox isolation level. When null, the tool's
    /// <see cref="ToolPermissionProfile.MinimumIsolation"/> is used.
    /// </summary>
    public SandboxIsolationLevel? IsolationLevelOverride { get; init; }
}

using Domain.Common.Config.AI;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.AI;

namespace Domain.AI.Skills;

/// <summary>
/// Options for creating agents from skill definitions.
/// Controls resource loading, deployment overrides, and additional configuration.
/// </summary>
/// <remarks>
/// Immutable: every property is init-only, so a scoped variant is produced with a
/// <c>with</c> expression (e.g. <c>options with { AdditionalProperties = ... }</c>)
/// rather than a hand-copied clone, which guarantees no property is silently dropped
/// as the shape grows.
/// </remarks>
public sealed record SkillAgentOptions
{
	#region Skill Loading

	/// <summary>
	/// Override the skill search paths for this agent creation.
	/// When null, paths from <c>AppConfig.AI.Skills</c> are used.
	/// </summary>
	public IList<string>? SkillPaths { get; init; }

	#endregion

	#region Agent Configuration

	/// <summary>
	/// Override the generated agent name.
	/// </summary>
	public string? AgentNameOverride { get; init; }

	/// <summary>
	/// Override the default deployment name.
	/// </summary>
	public string? DeploymentName { get; init; }

	/// <summary>
	/// Override the persistent agent ID from skill metadata.
	/// </summary>
	public string? AgentId { get; init; }

	/// <summary>
	/// Override the default framework type.
	/// </summary>
	public AIAgentFrameworkClientType? FrameworkType { get; init; }

	/// <summary>
	/// Additional context to append to instructions.
	/// </summary>
	public string? AdditionalContext { get; init; }

	/// <summary>
	/// The agent's own instructions (its system prompt), sourced from the <c>AGENT.md</c> body.
	/// When present, they lead the merged instruction text ahead of every skill. Null when the
	/// agent is invoked by a bare skill id with no owning <c>AGENT.md</c>.
	/// </summary>
	public string? AgentInstructions { get; init; }

	/// <summary>
	/// Override the sampling temperature for the underlying chat client.
	/// When null, the provider default is used.
	/// </summary>
	public float? Temperature { get; init; }

	/// <summary>
	/// Additional tools for the agent.
	/// </summary>
	public IList<AITool>? AdditionalTools { get; init; }

	/// <summary>
	/// Additional middleware types.
	/// </summary>
	public IList<Type>? MiddlewareTypes { get; init; }

	/// <summary>
	/// Additional properties for the agent definition.
	/// </summary>
	public IDictionary<string, object>? AdditionalProperties { get; init; }

	/// <summary>
	/// Optional trace scope for this run. When set, the factory uses this scope;
	/// otherwise <c>TraceScope.ForExecution(Guid.NewGuid())</c> is created.
	/// </summary>
	public TraceScope? TraceScope { get; init; }

	#endregion
}

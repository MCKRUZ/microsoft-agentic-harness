using Domain.Common.Config.AI;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.AI;

namespace Domain.AI.Skills;

/// <summary>
/// Options for creating agents from skill definitions.
/// Controls resource loading, deployment overrides, and additional configuration.
/// </summary>
public class SkillAgentOptions
{
	#region Resource Loading

	/// <summary>
	/// Load all templates and include them in agent instructions.
	/// </summary>
	public bool LoadAllTemplates { get; set; }

	/// <summary>
	/// Load all references and include them in agent instructions.
	/// </summary>
	public bool LoadAllReferences { get; set; }

	/// <summary>
	/// Specific templates to load by file name.
	/// </summary>
	public IList<string>? TemplatesToLoad { get; set; }

	/// <summary>
	/// Specific references to load by file name.
	/// </summary>
	public IList<string>? ReferencesToLoad { get; set; }

	/// <summary>
	/// Include loaded resources in agent instructions.
	/// </summary>
	public bool IncludeResourcesInInstructions { get; set; } = true;

	/// <summary>
	/// Include a manifest of available resources.
	/// </summary>
	public bool IncludeResourceManifest { get; set; } = true;

	#endregion

	#region Skill Loading

	/// <summary>
	/// Override the skill search paths for this agent creation.
	/// When null, paths from <c>AppConfig.AI.Skills</c> are used.
	/// </summary>
	public IList<string>? SkillPaths { get; set; }

	#endregion

	#region Agent Configuration

	/// <summary>
	/// Override the generated agent name.
	/// </summary>
	public string? AgentNameOverride { get; set; }

	/// <summary>
	/// Override the default deployment name.
	/// </summary>
	public string? DeploymentName { get; set; }

	/// <summary>
	/// Override the persistent agent ID from skill metadata.
	/// </summary>
	public string? AgentId { get; set; }

	/// <summary>
	/// Override the default framework type.
	/// </summary>
	public AIAgentFrameworkClientType? FrameworkType { get; set; }

	/// <summary>
	/// Additional context to append to instructions.
	/// </summary>
	public string? AdditionalContext { get; set; }

	/// <summary>
	/// Additional tools for the agent.
	/// </summary>
	public IList<AITool>? AdditionalTools { get; set; }

	/// <summary>
	/// Additional middleware types.
	/// </summary>
	public IList<Type>? MiddlewareTypes { get; set; }

	/// <summary>
	/// Additional properties for the agent definition.
	/// </summary>
	public IDictionary<string, object>? AdditionalProperties { get; set; }

	/// <summary>
	/// Optional trace scope for this run. When set, the factory uses this scope;
	/// otherwise <c>TraceScope.ForExecution(Guid.NewGuid())</c> is created.
	/// </summary>
	public TraceScope? TraceScope { get; set; }

	#endregion
}

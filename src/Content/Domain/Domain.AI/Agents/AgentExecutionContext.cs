using Domain.Common.Config.AI;
using Domain.Common.MetaHarness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.AI.Agents;

/// <summary>
/// Runtime execution context for an AI agent instance.
/// Passed to the Agent Framework to create a configured, running agent.
/// </summary>
/// <remarks>
/// <para><b>Relationship chain:</b></para>
/// <list type="bullet">
///   <item><b>AgentManifest</b> — declarative definition from AGENT.md (static, source-controlled)</item>
///   <item><b>SkillDefinition</b> — skill loaded from SKILL.md (progressive disclosure)</item>
///   <item><b>AgentExecutionContext</b> — runtime config for Agent Framework (dynamic, per-execution)</item>
///   <item><b>AIAgent</b> — the running agent instance created by the framework</item>
/// </list>
/// </remarks>
public class AgentExecutionContext
{
	/// <summary>
	/// Display name of the agent, used for identification, logging, and UI.
	/// </summary>
	public string? Name { get; set; }

	/// <summary>
	/// Behavioral instructions defining how the agent should respond and interact.
	/// Becomes the system prompt for the underlying LLM.
	/// </summary>
	public string? Instruction { get; set; }

	/// <summary>
	/// Description of the agent's purpose and capabilities.
	/// </summary>
	public string? Description { get; set; }

	/// <summary>
	/// Model deployment name (e.g., "gpt-4o", "gpt-4-turbo").
	/// Null to use the default deployment from config.
	/// </summary>
	public string? DeploymentName { get; set; }

	/// <summary>
	/// AI Foundry persistent agent ID. Required when
	/// <see cref="AIAgentFrameworkType"/> is <see cref="AIAgentFrameworkClientType.PersistentAgents"/>.
	/// </summary>
	public string? AgentId { get; set; }

	/// <summary>
	/// Which AI service provider to use (Azure OpenAI, OpenAI, Persistent Agents).
	/// </summary>
	public AIAgentFrameworkClientType AIAgentFrameworkType { get; set; } = AIAgentFrameworkClientType.AzureOpenAI;

	/// <summary>
	/// Tools available to the agent for function calling.
	/// </summary>
	public IList<AITool>? Tools { get; set; }

	/// <summary>
	/// Middleware types to apply to the agent's chat client pipeline.
	/// </summary>
	public IList<Type>? MiddlewareTypes { get; set; }

	/// <summary>
	/// AI context providers to invoke before and after each agent turn.
	/// Used for progressive skill disclosure, memory, and compaction.
	/// </summary>
	public IList<AIContextProvider>? AIContextProviders { get; set; }

	/// <summary>
	/// Trace scope for this execution run. Set by <c>AgentExecutionContextFactory</c>
	/// when an <c>IExecutionTraceStore</c> is wired in.
	/// </summary>
	public TraceScope? TraceScope { get; set; }

	/// <summary>
	/// Sampling temperature for the underlying chat client. Null preserves provider defaults.
	/// </summary>
	public float? Temperature { get; set; }

	/// <summary>
	/// Extensible configuration properties.
	/// </summary>
	public Dictionary<string, object>? AdditionalProperties { get; set; }
}

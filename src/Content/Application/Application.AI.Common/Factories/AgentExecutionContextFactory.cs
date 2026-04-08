using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Factories;

/// <summary>
/// Bridges declarative skill definitions (SKILL.md) to runtime <see cref="AgentExecutionContext"/>.
/// Handles tool provisioning (MCP-first, keyed DI fallback), instruction assembly, and middleware resolution.
/// </summary>
public class AgentExecutionContextFactory
{
	private readonly ILogger<AgentExecutionContextFactory> _logger;
	private readonly IOptionsMonitor<AppConfig> _appConfig;
	private readonly IServiceProvider _serviceProvider;
	private readonly IToolConverter? _toolConverter;
	private readonly IMcpToolProvider? _mcpToolProvider;
	private readonly IContextBudgetTracker? _budgetTracker;

	public AgentExecutionContextFactory(
		ILogger<AgentExecutionContextFactory> logger,
		IOptionsMonitor<AppConfig> appConfig,
		IServiceProvider serviceProvider,
		IToolConverter? toolConverter = null,
		IMcpToolProvider? mcpToolProvider = null,
		IContextBudgetTracker? budgetTracker = null)
	{
		_logger = logger;
		_appConfig = appConfig;
		_serviceProvider = serviceProvider;
		_toolConverter = toolConverter;
		_mcpToolProvider = mcpToolProvider;
		_budgetTracker = budgetTracker;
	}

	/// <summary>
	/// Maps a skill definition and options to a runtime agent execution context.
	/// </summary>
	public async Task<AgentExecutionContext> MapToAgentContextAsync(SkillDefinition skill, SkillAgentOptions options)
	{
		var deploymentName = ResolveDeploymentName(skill, options);
		var agentName = options.AgentNameOverride ?? ToAgentName(skill.Name);
		var instruction = BuildInstruction(skill, options);
		var tools = await BuildToolsAsync(skill, options);
		var middlewareTypes = ResolveMiddlewareTypes(skill, options);
		var frameworkType = options.FrameworkType ?? AIAgentFrameworkClientType.AzureOpenAI;

		// Track context budget allocations
		if (_budgetTracker != null)
		{
			var instructionTokens = TokenEstimationHelper.EstimateTokens(instruction);
			_budgetTracker.RecordAllocation(agentName, "system_prompt", instructionTokens);

			if (tools?.Count > 0)
			{
				// Rough estimate: ~50 tokens per tool for schema
				var toolTokens = tools.Count * 50;
				_budgetTracker.RecordAllocation(agentName, "tool_schemas", toolTokens);
			}
		}

		var context = new AgentExecutionContext
		{
			Name = agentName,
			Description = skill.Description,
			Instruction = instruction,
			DeploymentName = deploymentName,
			AgentId = options.AgentId ?? skill.AgentId,
			AIAgentFrameworkType = frameworkType,
			Tools = tools,
			MiddlewareTypes = middlewareTypes,
			AdditionalProperties = BuildAdditionalProperties(skill, options)
		};

		_logger.LogInformation(
			"Mapped skill {SkillId} to agent context {AgentName} with {ToolCount} tools",
			skill.Id, agentName, tools?.Count ?? 0);

		return context;
	}

	private string ResolveDeploymentName(SkillDefinition skill, SkillAgentOptions options)
	{
		// Priority: options > skill metadata > config default
		if (!string.IsNullOrEmpty(options.DeploymentName))
			return options.DeploymentName;

		if (!string.IsNullOrEmpty(skill.ModelOverride))
			return skill.ModelOverride;

		if (skill.Metadata?.TryGetValue("deployment", out var deployment) == true)
			return deployment.ToString() ?? "default";

		return _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment ?? "default";
	}

	private static string BuildInstruction(SkillDefinition skill, SkillAgentOptions options)
	{
		var parts = new List<string>();

		if (!string.IsNullOrEmpty(skill.Instructions))
			parts.Add(skill.Instructions);

		// Append loaded resource content if configured
		if (options.IncludeResourcesInInstructions)
		{
			foreach (var template in skill.Templates.Where(t => t.IsLoaded))
				parts.Add($"## Template: {template.FileName}\n{template.Content}");

			foreach (var reference in skill.References.Where(r => r.IsLoaded))
				parts.Add($"## Reference: {reference.FileName}\n{reference.Content}");
		}

		// Append resource manifest if configured
		if (options.IncludeResourceManifest && skill.TotalResourceCount > 0)
		{
			var manifest = new List<string> { "## Available Resources" };
			if (skill.HasTemplates)
				manifest.AddRange(skill.Templates.Select(t => $"- Template: {t.FileName}"));
			if (skill.HasReferences)
				manifest.AddRange(skill.References.Select(r => $"- Reference: {r.FileName}"));
			parts.Add(string.Join("\n", manifest));
		}

		if (!string.IsNullOrEmpty(options.AdditionalContext))
			parts.Add(options.AdditionalContext);

		return string.Join("\n\n", parts);
	}

	private async Task<List<AITool>> BuildToolsAsync(SkillDefinition skill, SkillAgentOptions options)
	{
		var tools = new List<AITool>();

		// 1. Pre-created tools from skill definition
		if (skill.Tools?.Count > 0)
			tools.AddRange(skill.Tools);

		// 2. Tools from declarations (MCP-first, keyed DI fallback) — resolved in parallel
		if (skill.ToolDeclarations?.Count > 0)
		{
			var provisionTasks = skill.ToolDeclarations.Select(ProvisionToolAsync);
			var results = await Task.WhenAll(provisionTasks);
			foreach (var provisioned in results)
			{
				if (provisioned != null)
					tools.AddRange(provisioned);
			}
		}

		// 3. Tools from AllowedTools list (simple name-based resolution)
		if (skill.AllowedTools?.Count > 0)
		{
			foreach (var toolName in skill.AllowedTools)
			{
				var resolved = ResolveToolByName(toolName);
				if (resolved != null)
					tools.AddRange(resolved);
			}
		}

		// 4. Additional tools from options
		if (options.AdditionalTools?.Count > 0)
			tools.AddRange(options.AdditionalTools);

		return tools;
	}

	private async Task<IEnumerable<AITool>?> ProvisionToolAsync(Domain.AI.Tools.ToolDeclaration declaration)
	{
		// Try MCP first
		if (_mcpToolProvider != null)
		{
			try
			{
				var mcpTools = await _mcpToolProvider.GetToolsAsync(declaration.Name);
				if (mcpTools?.Count > 0)
				{
					_logger.LogDebug("Resolved tool {ToolName} from MCP server", declaration.Name);
					return mcpTools;
				}
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "MCP resolution failed for {ToolName}, trying keyed DI", declaration.Name);
			}
		}

		// Fallback to keyed DI
		var resolved = ResolveToolByName(declaration.Name);
		if (resolved != null)
			return resolved;

		// Try fallback tool
		if (declaration.HasFallback && !declaration.FallbackIsManual)
		{
			resolved = ResolveToolByName(declaration.Fallback!);
			if (resolved != null)
			{
				_logger.LogInformation("Using fallback tool {Fallback} for {ToolName}",
					declaration.Fallback, declaration.Name);
				return resolved;
			}
		}

		if (!declaration.Optional)
			_logger.LogWarning("Required tool {ToolName} could not be resolved", declaration.Name);

		return null;
	}

	private IEnumerable<AITool>? ResolveToolByName(string toolName)
	{
		var tool = _serviceProvider.GetKeyedService<ITool>(toolName);
		if (tool == null)
			return null;

		if (_toolConverter != null)
		{
			var converted = _toolConverter.Convert(tool);
			if (converted != null)
				return [converted];
		}

		_logger.LogDebug("Resolved tool {ToolName} from keyed DI (no converter available)", toolName);
		return null;
	}

	private List<Type>? ResolveMiddlewareTypes(SkillDefinition skill, SkillAgentOptions options)
	{
		var types = new List<Type>();

		// Default middleware from config
		types.Add(typeof(Middleware.ObservabilityMiddleware));
		types.Add(typeof(Middleware.ToolDiagnosticsMiddleware));

		// Additional from options
		if (options.MiddlewareTypes?.Count > 0)
			types.AddRange(options.MiddlewareTypes);

		return types.Count > 0 ? types : null;
	}

	private static Dictionary<string, object> BuildAdditionalProperties(SkillDefinition skill, SkillAgentOptions options)
	{
		var props = new Dictionary<string, object>
		{
			["skillId"] = skill.Id,
			["skillName"] = skill.Name,
			["loadedAt"] = skill.LoadedAt.ToString("O")
		};

		if (!string.IsNullOrEmpty(skill.Category))
			props["category"] = skill.Category;
		if (skill.HasTags)
			props["tags"] = skill.Tags;
		if (!string.IsNullOrEmpty(skill.Version))
			props["version"] = skill.Version;

		if (skill.Metadata != null)
		{
			foreach (var (key, value) in skill.Metadata)
				props[$"skill_{key}"] = value;
		}

		if (options.AdditionalProperties != null)
		{
			foreach (var (key, value) in options.AdditionalProperties)
				props[key] = value;
		}

		return props;
	}

	private static string ToAgentName(string skillName)
	{
		// Convert "research-agent" to "ResearchAgent"
		var parts = skillName.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
		var pascal = string.Concat(parts.Select(p =>
			char.ToUpperInvariant(p[0]) + p[1..]));
		return pascal.EndsWith("Agent", StringComparison.OrdinalIgnoreCase)
			? pascal
			: pascal + "Agent";
	}
}

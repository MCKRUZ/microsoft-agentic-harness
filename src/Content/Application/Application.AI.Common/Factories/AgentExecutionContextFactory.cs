using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Resilience;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Models;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.MetaHarness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Factories;

/// <summary>
/// Bridges declarative skill definitions (SKILL.md) to runtime <see cref="AgentExecutionContext"/>.
/// Delegates tool provisioning to <see cref="IToolChainBuilder"/> and prerequisite resolution
/// to <see cref="ISkillPrerequisiteResolver"/>. Handles instruction assembly, middleware
/// resolution, budget tracking, and wiring of <see cref="AgentSkillsProvider"/> for progressive
/// skill disclosure.
/// </summary>
public class AgentExecutionContextFactory
{
    private static readonly AgentFileSkillScriptRunner NoOpScriptRunner =
        (skill, script, arguments, serviceProvider, cancellationToken) =>
            Task.FromResult<object?>(null);

    private readonly ILogger<AgentExecutionContextFactory> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IToolChainBuilder _toolChainBuilder;
    private readonly ISkillPrerequisiteResolver _prerequisiteResolver;
    private readonly IContextBudgetTracker? _budgetTracker;
    private readonly IExecutionTraceStore? _traceStore;
    private readonly ISkillContentProvider? _skillContentProvider;
    private readonly IAgentConfigReporter? _agentConfigReporter;
    private readonly IResilientChatClientProvider? _resilientChatClientProvider;

    public AgentExecutionContextFactory(
        ILogger<AgentExecutionContextFactory> logger,
        IOptionsMonitor<AppConfig> appConfig,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        IToolChainBuilder toolChainBuilder,
        ISkillPrerequisiteResolver prerequisiteResolver,
        IContextBudgetTracker? budgetTracker = null,
        IExecutionTraceStore? traceStore = null,
        ISkillContentProvider? skillContentProvider = null,
        IAgentConfigReporter? agentConfigReporter = null,
        IResilientChatClientProvider? resilientChatClientProvider = null)
    {
        _logger = logger;
        _appConfig = appConfig;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _toolChainBuilder = toolChainBuilder;
        _prerequisiteResolver = prerequisiteResolver;
        _budgetTracker = budgetTracker;
        _traceStore = traceStore;
        _skillContentProvider = skillContentProvider;
        _agentConfigReporter = agentConfigReporter;
        _resilientChatClientProvider = resilientChatClientProvider;
    }

    /// <summary>
    /// Maps a single skill definition and options to a runtime agent execution context.
    /// Delegates to the multi-skill overload.
    /// </summary>
    public Task<AgentExecutionContext> MapToAgentContextAsync(SkillDefinition skill, SkillAgentOptions options)
        => MapToAgentContextAsync([skill], options);

    /// <summary>
    /// Maps multiple skill definitions to a single agent execution context by merging
    /// instructions, tools, and context providers from all skills. The first skill is
    /// used as the primary for deployment resolution, agent ID, and additional properties.
    /// </summary>
    /// <param name="skills">The skill definitions to merge.</param>
    /// <param name="options">Configuration for resource loading and agent overrides.</param>
    /// <param name="allowedTools">Optional tool allowlist applied after merge — only tools with matching names are kept.</param>
    public virtual async Task<AgentExecutionContext> MapToAgentContextAsync(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? allowedTools = null)
    {
        if (skills.Count == 0)
            throw new ArgumentException("At least one skill is required.", nameof(skills));

        var primarySkill = skills[0];
        var deploymentName = ResolveDeploymentName(primarySkill, options);
        var agentName = options.AgentNameOverride ?? ToAgentName(primarySkill.Name);
        var instruction = BuildMergedInstruction(skills, options);
        var tools = await _toolChainBuilder.BuildMergedToolsAsync(skills, options, allowedTools);
        var middlewareTypes = ResolveMiddlewareTypes(primarySkill, options);
        var aiContextProviders = BuildMergedAIContextProviders(skills, options);
        var frameworkType = options.FrameworkType
            ?? ResolveFrameworkTypeFromMetadata(primarySkill)
            ?? _appConfig.CurrentValue.AI?.AgentFramework?.ClientType
            ?? AIAgentFrameworkClientType.AzureOpenAI;

        // Resolve or create a trace scope for this execution
        var traceScope = options.TraceScope ?? TraceScope.ForExecution(Guid.NewGuid());

        // Track context budget allocations
        if (_budgetTracker != null)
        {
            var instructionTokens = TokenEstimationHelper.EstimateTokens(instruction);
            _budgetTracker.RecordAllocation(agentName, "system_prompt", instructionTokens);

            ContextBudgetMetrics.SystemPromptTokens.Record(instructionTokens,
                new KeyValuePair<string, object?>(AgentConventions.Name, agentName));
            ContextSourceMetrics.SourceTokens.Record(instructionTokens,
                new KeyValuePair<string, object?>(ContextConventions.SourceType, ContextConventions.SourceTypeValues.SystemPrompt),
                new KeyValuePair<string, object?>(AgentConventions.Name, agentName));

            if (tools?.Count > 0)
            {
                var toolTokens = tools.Count * 50; // ~50 tokens per tool schema
                _budgetTracker.RecordAllocation(agentName, "tool_schemas", toolTokens);

                ContextBudgetMetrics.ToolsSchemaTokens.Record(toolTokens,
                    new KeyValuePair<string, object?>(AgentConventions.Name, agentName));
                ContextSourceMetrics.SourceTokens.Record(toolTokens,
                    new KeyValuePair<string, object?>(ContextConventions.SourceType, ContextConventions.SourceTypeValues.ToolsSchema),
                    new KeyValuePair<string, object?>(AgentConventions.Name, agentName));
            }
        }

        var additionalProps = BuildAdditionalProperties(primarySkill, options);

        // Compute prerequisite map for middleware consumption
        var prerequisiteMap = _prerequisiteResolver.BuildPrerequisiteMap(skills, tools);
        if (prerequisiteMap.HasAnyPrerequisites)
            additionalProps[SkillPrerequisiteMap.AdditionalPropertiesKey] = prerequisiteMap;

        // Expose candidate skill content provider so evaluation contexts can inject candidate content
        if (_skillContentProvider != null)
            additionalProps[ISkillContentProvider.AdditionalPropertiesKey] = _skillContentProvider;

        // Stash resilient chat client for transparent fallback when resilience is enabled
        if (_resilientChatClientProvider is not null)
        {
            var resilientClient = await _resilientChatClientProvider.GetResilientChatClientAsync();
            additionalProps["__resilientChatClient"] = resilientClient;
        }

        // Start a trace run when a store is wired in
        if (_traceStore != null)
        {
            var metadata = new RunMetadata
            {
                AgentName = agentName,
                StartedAt = DateTimeOffset.UtcNow
            };
            var traceWriter = await _traceStore.StartRunAsync(traceScope, metadata);
            additionalProps[ITraceWriter.AdditionalPropertiesKey] = traceWriter;

            // Set candidate baggage on the current Activity for CausalSpanAttributionProcessor
            if (traceScope.CandidateId.HasValue)
            {
                System.Diagnostics.Activity.Current?.AddBaggage(
                    Domain.AI.Telemetry.Conventions.ToolConventions.HarnessCandidateId,
                    traceScope.CandidateId.Value.ToString("D"));
            }
        }

        var context = new AgentExecutionContext
        {
            Name = agentName,
            Description = primarySkill.Description,
            Instruction = instruction,
            DeploymentName = deploymentName,
            AgentId = options.AgentId ?? primarySkill.AgentId,
            AIAgentFrameworkType = frameworkType,
            Tools = tools,
            AIContextProviders = aiContextProviders,
            MiddlewareTypes = middlewareTypes,
            TraceScope = traceScope,
            Temperature = options.Temperature,
            AdditionalProperties = additionalProps
        };

        _agentConfigReporter?.RegisterAgent(
            agentName,
            deploymentName,
            (options.Temperature ?? 0.7).ToString("0.##"),
            tools?.Count ?? 0,
            aiContextProviders?.Count ?? 0,
            _toolChainBuilder is not null ? 1 : 0);

        _logger.LogInformation(
            "Mapped {SkillCount} skill(s) to agent context {AgentName} with {ToolCount} tools and {ProviderCount} context providers",
            skills.Count, agentName, tools?.Count ?? 0, aiContextProviders?.Count ?? 0);

        return context;
    }

    /// <summary>
    /// Creates an execution context for a delegated agent. Used by <see cref="Interfaces.Agents.ISupervisor"/>
    /// when delegating a task. Bypasses skill-based tool resolution — tools are resolved separately
    /// by the supervisor using <see cref="Interfaces.Agents.ISubagentToolResolver"/>.
    /// </summary>
    public AgentExecutionContext CreateFromDelegation(
        SubagentDefinition definition,
        IReadOnlyList<string>? toolOverrides,
        int delegationDepth,
        Guid delegationId)
    {
        var deploymentName = definition.ModelOverride
            ?? _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment
            ?? "default";

        var context = new AgentExecutionContext
        {
            Name = definition.AgentType + "Agent",
            Instruction = definition.SystemPromptOverride,
            DeploymentName = deploymentName,
            DelegationDepth = delegationDepth,
            DelegationId = delegationId,
            DelegatingAgentType = definition.AgentType,
            AdditionalProperties = new Dictionary<string, object>()
        };

        if (toolOverrides is { Count: > 0 })
            context.AdditionalProperties["delegationToolOverrides"] = toolOverrides;

        return context;
    }

    private static AIAgentFrameworkClientType? ResolveFrameworkTypeFromMetadata(SkillDefinition skill)
    {
        if (skill.Metadata?.TryGetValue("framework_type", out var value) == true
            && Enum.TryParse<AIAgentFrameworkClientType>(value?.ToString(), ignoreCase: true, out var parsed))
            return parsed;

        return null;
    }

    private string ResolveDeploymentName(SkillDefinition skill, SkillAgentOptions options)
    {
        if (!string.IsNullOrEmpty(options.DeploymentName))
            return options.DeploymentName;

        if (!string.IsNullOrEmpty(skill.ModelOverride))
            return skill.ModelOverride;

        if (skill.Metadata?.TryGetValue("deployment", out var deployment) == true)
            return deployment.ToString() ?? "default";

        return _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment ?? "default";
    }

    /// <summary>
    /// Merges instructions from all skills into a single instruction string.
    /// When multiple skills are present, each skill's instructions are wrapped with a section header.
    /// A single skill's instructions are used as-is without headers.
    /// </summary>
    private static string BuildMergedInstruction(IReadOnlyList<SkillDefinition> skills, SkillAgentOptions options)
    {
        var parts = new List<string>();

        foreach (var skill in skills)
        {
            if (string.IsNullOrEmpty(skill.Instructions))
                continue;

            if (skills.Count > 1)
                parts.Add($"## Skill: {skill.Name}\n\n{skill.Instructions}");
            else
                parts.Add(skill.Instructions);
        }

        if (!string.IsNullOrEmpty(options.AdditionalContext))
            parts.Add(options.AdditionalContext);

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Unions context providers from all skills. Skill paths are resolved once (from options or config).
    /// AllowedTools constraints from all skills are merged into a single <see cref="Services.Agent.ToolPermissionFilter"/>.
    /// </summary>
    private IList<AIContextProvider>? BuildMergedAIContextProviders(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options)
    {
        var providers = new List<AIContextProvider>();

        var skillPaths = ResolveSkillPaths(options);

        if (skillPaths.Count > 0)
        {
            var builder = new AgentSkillsProviderBuilder()
                .UseFileScriptRunner(NoOpScriptRunner);
            foreach (var path in skillPaths)
                builder.UseFileSkill(path);
            providers.Add(builder.Build());

            _logger.LogDebug("Wired AgentSkillsProvider with {PathCount} path(s)", skillPaths.Count);
        }

        var allAllowedTools = skills
            .Where(s => s.AllowedTools?.Count > 0)
            .SelectMany(s => s.AllowedTools!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allAllowedTools.Count > 0)
        {
            providers.Add(new Services.Agent.ToolPermissionFilter(allAllowedTools));

            _logger.LogDebug("Wired ToolPermissionFilter with {Count} allowed tool(s) from {SkillCount} skill(s)",
                allAllowedTools.Count, skills.Count);
        }

        return providers.Count > 0 ? providers : null;
    }

    private IReadOnlyList<string> ResolveSkillPaths(SkillAgentOptions options)
    {
        if (options.SkillPaths?.Count > 0)
            return [.. options.SkillPaths];

        var configPaths = _appConfig.CurrentValue.AI?.Skills?.AllPaths.ToList() ?? [];
        return configPaths
            .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(p))
            .Where(Directory.Exists)
            .ToList();
    }

    private List<Type>? ResolveMiddlewareTypes(SkillDefinition skill, SkillAgentOptions options)
    {
        var types = new List<Type>();

        types.Add(typeof(Middleware.ObservabilityMiddleware));
        types.Add(typeof(Middleware.ToolDiagnosticsMiddleware));

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
        var parts = skillName.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        var pascal = string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..]));
        return pascal.EndsWith("Agent", StringComparison.OrdinalIgnoreCase)
            ? pascal
            : pascal + "Agent";
    }
}

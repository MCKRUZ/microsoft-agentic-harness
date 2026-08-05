using Application.AI.Common.Extensions;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Factories;

/// <summary>
/// Bridges declarative skill definitions (SKILL.md) to runtime <see cref="AgentExecutionContext"/>.
/// Delegates tool provisioning to <see cref="IToolChainBuilder"/> and prerequisite resolution
/// to <see cref="ISkillPrerequisiteResolver"/>. Handles instruction assembly, middleware
/// resolution, budget tracking, and wiring of <c>AgentSkillsProvider</c> for progressive
/// skill disclosure.
/// </summary>
/// <remarks>
/// <para>
/// Split across partials by responsibility, with this file holding the construction dependencies and
/// the two public entry points that orchestrate them:
/// </para>
/// <list type="bullet">
///   <item><c>AgentExecutionContextFactory.Prompt.cs</c> — authoritative static system prompt composition.</item>
///   <item><c>AgentExecutionContextFactory.SkillDisclosure.cs</c> — progressive-disclosure budget charging and fallback reporting.</item>
///   <item><c>AgentExecutionContextFactory.ContextProviders.cs</c> — the ordered <c>AIContextProvider</c> rail.</item>
///   <item><c>AgentExecutionContextFactory.Resolution.cs</c> — deployment, framework, tool-ceiling, middleware, and naming resolvers.</item>
/// </list>
/// </remarks>
public partial class AgentExecutionContextFactory
{
    private readonly ILogger<AgentExecutionContextFactory> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IToolChainBuilder _toolChainBuilder;
    private readonly ISkillPrerequisiteResolver _prerequisiteResolver;
    private readonly ISkillFileReader _skillFileReader;
    private readonly IContextBudgetTracker? _budgetTracker;
    private readonly IExecutionTraceStore? _traceStore;
    private readonly IAgentConfigReporter? _agentConfigReporter;
    private readonly IResilientChatClientProvider? _resilientChatClientProvider;

    public AgentExecutionContextFactory(
        ILogger<AgentExecutionContextFactory> logger,
        IOptionsMonitor<AppConfig> appConfig,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        IToolChainBuilder toolChainBuilder,
        ISkillPrerequisiteResolver prerequisiteResolver,
        ISkillFileReader skillFileReader,
        IContextBudgetTracker? budgetTracker = null,
        IExecutionTraceStore? traceStore = null,
        IAgentConfigReporter? agentConfigReporter = null,
        IResilientChatClientProvider? resilientChatClientProvider = null)
    {
        ArgumentNullException.ThrowIfNull(skillFileReader);

        _logger = logger;
        _appConfig = appConfig;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _toolChainBuilder = toolChainBuilder;
        _prerequisiteResolver = prerequisiteResolver;
        _skillFileReader = skillFileReader;
        _budgetTracker = budgetTracker;
        _traceStore = traceStore;
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
    /// <param name="allowedTools">
    /// Optional explicit per-call tool ceiling, applied on top of the skills' allowlist and the agent's
    /// declared ceiling. It can only tighten (never widen) the effective set. <see langword="null"/> or
    /// empty means "no extra ceiling from this call"; a non-empty list caps the agent to the intersection.
    /// </param>
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

        // One list drives two decisions that must never disagree: which skills the framework provider is
        // given, and which skills may therefore omit their body from the static prompt. Because the prompt's
        // decision is read off the very set that gets registered, the two cannot drift apart.
        var disclosableSkills = DisclosableSkillFactory.Create(skills, _skillFileReader, _logger);
        var disclosedOnDemand = disclosableSkills
            .Select(s => s.SkillId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        LogSkillsExcludedFromDisclosure(skills, disclosedOnDemand);

        // Everything above defers cost to pulls that happen inside the framework provider; this is what
        // keeps the budget recorded below able to see them. See BudgetChargingSkill for why (issue #248).
        disclosableSkills = ChargeSkillLoadsToBudget(disclosableSkills, agentName);

        // Static system prompt. The legacy path merges skill instructions + additional context
        // verbatim (SkillInstructionMerger is the single source of truth for that format). Bodies the
        // framework provider will serve through load_skill are omitted — that is Tier 2 content, and the
        // provider already advertises the Tier 1 name/description index card for them. When
        // PromptComposition is enabled, the authoritative section composer reframes that same skill
        // content with identity + permission-rules sections within a token budget; per-turn dynamic
        // context (session state, memory) stays on the AIContextProvider rail, never baked in here.
        var instruction = SkillInstructionMerger.Merge(
            skills, options.AdditionalContext, options.AgentInstructions, disclosedOnDemand);
        if (_appConfig.CurrentValue.AI?.ContextManagement?.PromptComposition?.Enabled == true)
            instruction = await ComposeStaticSystemPromptAsync(agentName, instruction);

        // Agent tool ceiling. Resolve the one effective allowlist that governs this agent (see
        // ResolveEffectiveAllowlist) and drive BOTH enforcement points with it — the merge-time tool
        // filter and the runtime ToolPermissionFilter — so they can never disagree. null means no
        // restriction (every tool passes); a non-null list is an active restriction (empty = deny all).
        var effectiveAllowedTools = ResolveEffectiveAllowlist(skills, options, allowedTools);
        var mergedToolChain = await _toolChainBuilder.BuildMergedToolsWithSourcesAsync(skills, options, effectiveAllowedTools);
        var tools = mergedToolChain.Tools.ToList();
        var middlewareTypes = ResolveMiddlewareTypes(primarySkill, options);
        var aiContextProviders = BuildMergedAIContextProviders(skills.Count, effectiveAllowedTools, disclosableSkills);

        // Charges what that rail injects into every turn — see AppendPerTurnBudgetProvider (issue #266).
        AppendPerTurnBudgetProvider(aiContextProviders, agentName, instruction, tools.Count);
        var frameworkType = options.FrameworkType
            ?? ResolveFrameworkTypeFromMetadata(primarySkill)
            ?? _appConfig.CurrentValue.AI?.AgentFramework?.ClientType
            ?? AIAgentFrameworkClientType.AzureOpenAI;

        // Resolve or create a trace scope for this execution
        var traceScope = options.TraceScope ?? TraceScope.ForExecution(Guid.NewGuid());

        // Track context budget allocations
        if (_budgetTracker != null)
        {
            _budgetTracker.RecordAndPublish(
                agentName,
                ContextConventions.BudgetComponents.SystemPrompt,
                ContextConventions.SourceTypeValues.SystemPrompt,
                TokenEstimationHelper.EstimateTokens(instruction),
                ContextBudgetMetrics.SystemPromptTokens);

            if (tools?.Count > 0)
            {
                _budgetTracker.RecordAndPublish(
                    agentName,
                    ContextConventions.BudgetComponents.ToolSchemas,
                    ContextConventions.SourceTypeValues.ToolsSchema,
                    TokenEstimationHelper.EstimateToolSchemaTokens(tools.Count),
                    ContextBudgetMetrics.ToolsSchemaTokens);
            }
        }

        var additionalProps = BuildAdditionalProperties(primarySkill, options);

        // Compute prerequisite map for middleware consumption
        // non-null: `tools` is the result of ToList() at method start; the ?. usages elsewhere are defensive only
        var prerequisiteMap = _prerequisiteResolver.BuildPrerequisiteMap(skills, tools!);
        if (prerequisiteMap.HasAnyPrerequisites)
            additionalProps[SkillPrerequisiteMap.AdditionalPropertiesKey] = prerequisiteMap;

        // Stash the composed resilient chat client for AgentFactory to consume. Gated on:
        // (a) ResilienceConfig.Enabled — when off the provider would return the PRIMARY raw
        //     client, which must not override the per-context resolution above; and
        // (b) ResilientClientEligibility — the fallback chain can only stand in for a context
        //     that resolved to exactly the primary configured provider + default deployment.
        //     Per-skill/per-options overrides, PersistentAgents (AgentId-bound), FoundryResponses,
        //     and Echo contexts keep their raw client.
        if (_resilientChatClientProvider is not null
            && _appConfig.CurrentValue.AI?.Resilience?.Enabled == true)
        {
            if (ResilientClientEligibility.IsEligible(
                    frameworkType, deploymentName, _appConfig.CurrentValue.AI?.AgentFramework))
            {
                var resilientClient = await _resilientChatClientProvider.GetResilientChatClientAsync();
                additionalProps[IResilientChatClientProvider.AdditionalPropertiesKey] = resilientClient;

                _logger.LogDebug("Stashed resilient chat client (fallback chain) for agent {AgentName}", agentName);
            }
            else
            {
                _logger.LogDebug(
                    "Resilience enabled but agent {AgentName} keeps its raw client: resolved {FrameworkType}/{Deployment} is not the primary configured provider/deployment",
                    agentName, frameworkType, deploymentName);
            }
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
            McpToolNames = mergedToolChain.McpToolNames,
            SkillIds = skills.Select(s => s.Id).ToList(),
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

        // Provision the subagent's tools so a delegated agent can actually use them, not just generate
        // text. Names come from the caller's explicit override when supplied, otherwise the profile's
        // own ToolAllowlist; the profile's ToolDenylist is then subtracted. They resolve from keyed DI
        // through the same builder (convert + governance-wrap) the skill-based paths use. A null/empty
        // allowlist — an "inherit everything" profile such as Execute/General — provisions nothing here:
        // there is no parent tool pool to inherit from on the delegation path, so such subagents run
        // generation-only unless the caller passes explicit tool overrides.
        var requested = (toolOverrides is { Count: > 0 } ? toolOverrides : definition.ToolAllowlist) ?? [];
        var toolNames = definition.ToolDenylist is { Count: > 0 } denylist
            ? requested.Where(n => !denylist.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList()
            : requested;

        if (toolNames.Count > 0)
            context.Tools = _toolChainBuilder.BuildToolsByName(toolNames);

        return context;
    }
}

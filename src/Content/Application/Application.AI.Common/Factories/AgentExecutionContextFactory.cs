using Application.AI.Common.Extensions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Prompts;
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
using Microsoft.Extensions.DependencyInjection;
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
                    tools.Count * 50, // ~50 tokens per tool schema
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
    /// Builds the authoritative static system prompt via the scoped <see cref="ISystemPromptComposer"/>
    /// when <c>PromptComposition</c> is enabled. Fails open to <paramref name="legacyInstruction"/>
    /// (never throws): if no request scope is active, the composer/accessor cannot be resolved, or
    /// composition faults or yields empty, the legacy merged instruction is returned unchanged.
    /// </summary>
    /// <remarks>
    /// The factory is a singleton while the composer and its section providers are scoped, so the
    /// scoped services are resolved per invocation from the current request scope via
    /// <see cref="IAmbientRequestScope"/> — the same idiom used for the Knowledge/Learnings context
    /// providers. Only the authoritative static section types
    /// (<see cref="AuthoritativePromptSections.Default"/>) are composed; per-turn dynamic sections are
    /// deliberately excluded and remain on the <c>AIContextProvider</c> rail.
    /// </remarks>
    private async Task<string> ComposeStaticSystemPromptAsync(string agentName, string legacyInstruction)
    {
        var scope = _serviceProvider.GetService<IAmbientRequestScope>()?.Current;
        if (scope is null)
        {
            _logger.LogDebug(
                "PromptComposition enabled but no ambient request scope is active; using legacy instruction for {AgentName}",
                agentName);
            return legacyInstruction;
        }

        var composer = scope.GetService<ISystemPromptComposer>();
        var accessor = scope.GetService<ISkillInstructionAccessor>();
        if (composer is null || accessor is null)
        {
            _logger.LogDebug(
                "PromptComposition enabled but composer/accessor unavailable in the request scope; using legacy instruction for {AgentName}",
                agentName);
            return legacyInstruction;
        }

        try
        {
            // Source the current agent's merged skill instructions into the scoped section provider.
            accessor.Set(legacyInstruction);

            var budget = _appConfig.CurrentValue.AI?.ContextManagement?.PromptComposition?.TokenBudget ?? 8000;
            var composed = await composer.ComposeAsync(agentName, budget, AuthoritativePromptSections.Default);

            if (string.IsNullOrEmpty(composed))
            {
                _logger.LogDebug(
                    "PromptComposition produced an empty prompt for {AgentName}; using legacy instruction",
                    agentName);
                return legacyInstruction;
            }

            _logger.LogDebug("Composed authoritative static system prompt for agent {AgentName}", agentName);
            return composed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PromptComposition failed for agent {AgentName}; falling back to legacy instruction",
                agentName);
            return legacyInstruction;
        }
    }

    /// <summary>
    /// Resolves the single effective tool allowlist that governs an agent: the union of its skills'
    /// <c>AllowedTools</c> constraints, capped by the agent's declared ceiling (<paramref name="options"/>'s
    /// <see cref="SkillAgentOptions.AllowedTools"/>) and then by any explicit per-call
    /// <paramref name="explicitAllowlist"/>. Each cap can only tighten (see <see cref="ToolCeilingResolver"/>).
    /// Returns <see langword="null"/> when nothing restricts the agent (every tool is permitted); a
    /// non-null list is an active restriction, and an empty one denies every tool.
    /// </summary>
    private static IReadOnlyList<string>? ResolveEffectiveAllowlist(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? explicitAllowlist)
    {
        var effective = ToolCeilingResolver.ApplyCeiling(MergeSkillAllowedTools(skills), options.AllowedTools);
        return ToolCeilingResolver.ApplyCeiling(effective, explicitAllowlist);
    }

    /// <summary>
    /// Deduplicated union of every skill's <c>AllowedTools</c> constraint, case-insensitively, or
    /// <see langword="null"/> when no skill declares a constraint — the "unbounded" input the ceiling
    /// resolver expects for "no restriction" (distinct from an empty list, which means deny all).
    /// </summary>
    private static IReadOnlyList<string>? MergeSkillAllowedTools(IReadOnlyList<SkillDefinition> skills)
    {
        var union = skills
            .Where(s => s.AllowedTools?.Count > 0)
            .SelectMany(s => s.AllowedTools!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return union.Count > 0 ? union : null;
    }

    /// <summary>
    /// Unions the context providers for this agent over <paramref name="disclosableSkills"/>, which the
    /// caller built once so this wiring and the prompt's disclosure decision cannot disagree.
    /// The <paramref name="effectiveAllowlist"/> (the skills' combined constraint already capped by any
    /// agent tool ceiling) drives a single <see cref="Services.Agent.ToolPermissionFilter"/>. It is
    /// <see langword="null"/> when no restriction is active (no filter is wired), or a concrete set —
    /// possibly empty, meaning deny-all — when a restriction applies.
    /// </summary>
    private IList<AIContextProvider>? BuildMergedAIContextProviders(
        int skillCount,
        IReadOnlyList<string>? effectiveAllowlist,
        IReadOnlyList<DisclosableSkill> disclosableSkills)
    {
        var providers = new List<AIContextProvider>();

        if (disclosableSkills.Count > 0)
        {
            // Registering the agent's own skills, rather than a directory to search, is what keeps
            // load_skill from advertising skills this agent was never assigned.
            providers.Add(new AgentSkillsProviderBuilder()
                .UseSkills(disclosableSkills.Select(s => s.Skill))
                .UseOptions(SkillDisclosureDefaults.Configure)
                .Build());

            _logger.LogDebug("Wired AgentSkillsProvider with {SkillCount} skill(s)", disclosableSkills.Count);
        }

        // Placed immediately after the skills provider so it sees the framework's disclosure tools. Note
        // what this position does and does not guarantee: the framework feeds each provider the previous
        // one's output, so the filter's removals survive into everything added below. But a provider added
        // *after* this line whose own contribution introduces a tool would introduce it unfiltered — the
        // filter has already run. Any future tool-contributing provider belongs above this line.
        if (effectiveAllowlist is not null)
        {
            providers.Add(new Services.Agent.ToolPermissionFilter(effectiveAllowlist));

            _logger.LogDebug("Wired ToolPermissionFilter with {Count} allowed tool(s) for {SkillCount} skill(s)",
                effectiveAllowlist.Count, skillCount);
        }

        // Cross-session memory recall. The provider resolves tenant-aware IKnowledgeMemory per
        // invocation from the current request scope (via IAmbientRequestScope), so it is safe to
        // attach to a singleton-cached agent.
        if (_appConfig.CurrentValue.AI?.KnowledgeBridge?.Enabled == true)
        {
            var ambientScope = _serviceProvider.GetService<IAmbientRequestScope>();
            if (ambientScope is not null)
            {
                providers.Add(new Services.Agent.KnowledgeMemoryContextProvider(
                    ambientScope,
                    _appConfig,
                    _loggerFactory.CreateLogger<Services.Agent.KnowledgeMemoryContextProvider>()));

                _logger.LogDebug("Wired KnowledgeMemoryContextProvider for cross-session recall");
            }
        }

        // Task-similarity learnings recall. Like the memory provider above, it resolves the scoped,
        // tenant-aware ILearningRecaller per invocation from the current request scope, so it is safe to
        // attach to a singleton-cached agent. Injects the most task-relevant lessons (every source,
        // including work-memory synthesis output) at turn start — the read half of the self-improving loop.
        if (_appConfig.CurrentValue.AI?.LearningsRecall?.Enabled == true)
        {
            var ambientScope = _serviceProvider.GetService<IAmbientRequestScope>();
            if (ambientScope is not null)
            {
                providers.Add(new Services.Agent.LearningsRecallContextProvider(
                    ambientScope,
                    _appConfig,
                    _loggerFactory.CreateLogger<Services.Agent.LearningsRecallContextProvider>()));

                _logger.LogDebug("Wired LearningsRecallContextProvider for task-similarity recall");
            }
        }

        // Governance wrapper — added LAST so it wraps the final, filtered tool set. When
        // tool-invocation enforcement is on, this guarantees the governor gates every tool the agent
        // can call, including framework progressive-disclosure tools that bypass ToolChainBuilder.
        // Inert (and skipped entirely) when enforcement is off, so default behaviour is unchanged.
        if (_appConfig.CurrentValue.AI?.Governance?.EnforceToolInvocation == true)
        {
            providers.Add(new Services.Agent.GoverningToolContextProvider(
                _loggerFactory.CreateLogger<Services.Agent.GoverningToolContextProvider>()));
            _logger.LogDebug("Wired GoverningToolContextProvider (tool-invocation enforcement enabled)");
        }

        return providers.Count > 0 ? providers : null;
    }

    /// <summary>
    /// Wraps each disclosable skill so the tokens its Tier 2 body and Tier 3 files put into the context are
    /// charged to <paramref name="agentName"/>'s budget when the model pulls them.
    /// </summary>
    /// <param name="disclosableSkills">The skills about to be handed to the framework provider.</param>
    /// <param name="agentName">The agent whose budget the pulls are charged to.</param>
    /// <returns>
    /// The same skills, each wrapped — or the input unchanged when no budget tracker is wired in, so a host
    /// that does not track context passes the framework's own skill objects through untouched.
    /// </returns>
    private IReadOnlyList<DisclosableSkill> ChargeSkillLoadsToBudget(
        IReadOnlyList<DisclosableSkill> disclosableSkills,
        string agentName)
    {
        if (_budgetTracker is null || disclosableSkills.Count == 0)
            return disclosableSkills;

        return [.. disclosableSkills.Select(s => s with
        {
            Skill = new Services.Skills.BudgetChargingSkill(s.Skill, agentName, _budgetTracker)
        })];
    }

    /// <summary>
    /// Records which skills kept their full body in the static prompt because the framework provider will
    /// not serve them on demand.
    /// </summary>
    /// <remarks>
    /// Falling back to eager injection is the safe outcome, but it is also invisible — the agent works,
    /// the prompt is just larger than it should be. Without this line the only symptom of a skill that can
    /// no longer be registered (a name edited out of kebab-case, a description deleted) is a gradual return
    /// of the token cost progressive disclosure exists to remove. <see cref="DisclosableSkillFactory"/>
    /// names a reason for the skills it rejects; it stays silent about ones it never considered, so this
    /// total is the only signal covering those.
    /// </remarks>
    private void LogSkillsExcludedFromDisclosure(
        IReadOnlyList<SkillDefinition> skills,
        IReadOnlySet<string> disclosedOnDemand)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;

        var eager = skills
            .Where(s => !string.IsNullOrEmpty(s.Instructions) && !disclosedOnDemand.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();

        if (eager.Count == 0)
            return;

        _logger.LogDebug(
            "Skill instructions kept in the static prompt for {Count} skill(s) not registered with the " +
            "framework skills provider: {SkillIds}",
            eager.Count, string.Join(", ", eager));
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

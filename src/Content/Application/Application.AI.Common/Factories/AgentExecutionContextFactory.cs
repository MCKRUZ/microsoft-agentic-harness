using Application.AI.Common.Extensions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Governance;
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
/// <remarks>
/// <para>
/// Split across partials by responsibility, with this file holding the construction dependencies and
/// the two public entry points that orchestrate them:
/// </para>
/// <list type="bullet">
///   <item><c>AgentExecutionContextFactory.Prompt.cs</c> — authoritative static system prompt composition.</item>
///   <item><c>AgentExecutionContextFactory.SkillDisclosure.cs</c> — progressive-disclosure budget charging and fallback reporting.</item>
///   <item><c>AgentExecutionContextFactory.ContextProviders.cs</c> — the ordered <c>AIContextProvider</c> rail.</item>
///   <item><c>AgentExecutionContextFactory.Resolution.cs</c> — deployment, framework, tool-ceiling, middleware, additional-property, and naming decisions.</item>
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
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContextBudgetTracker? _budgetTracker;
    private readonly IExecutionTraceStore? _traceStore;
    private readonly IAgentConfigReporter? _agentConfigReporter;
    private readonly IResilientChatClientProvider? _resilientChatClientProvider;

    /// <summary>Initializes a new instance of the <see cref="AgentExecutionContextFactory"/> class.</summary>
    /// <param name="logger">Records factory-level diagnostics.</param>
    /// <param name="appConfig">The harness's live configuration, read for deployment/framework defaults.</param>
    /// <param name="serviceProvider">
    /// Resolves late-bound, host-optional dependencies this factory does not take as first-class
    /// constructor parameters — see <c>AgentExecutionContextFactory.ContextProviders.cs</c> for the
    /// pattern (e.g. <c>GetService&lt;IAmbientRequestScope&gt;()</c>).
    /// </param>
    /// <param name="loggerFactory">Creates the per-provider loggers each <c>AIContextProvider</c> on the rail needs.</param>
    /// <param name="toolChainBuilder">Provisions and governs the tool set an agent's context carries.</param>
    /// <param name="prerequisiteResolver">Resolves a multi-skill agent's prerequisite ordering.</param>
    /// <param name="skillFileReader">Reads skill files for progressive disclosure. Required — never null.</param>
    /// <param name="sanitizer">
    /// Passed to <see cref="Services.Agent.GoverningToolContextProvider"/> so it can scrub the output of
    /// the two skill-content transport tools it exempts from full governance wrapping (#480) — see that
    /// type's remarks for why a capability-grant exemption is not also a sanitization exemption.
    /// </param>
    /// <param name="budgetTracker">Tracks per-turn context spend, when the host wires one in.</param>
    /// <param name="traceStore">Persists per-turn execution traces, when the host wires one in.</param>
    /// <param name="agentConfigReporter">Reports the resolved agent configuration, when the host wires one in.</param>
    /// <param name="resilientChatClientProvider">Supplies a resilience-wrapped chat client, when the host wires one in.</param>
    public AgentExecutionContextFactory(
        ILogger<AgentExecutionContextFactory> logger,
        IOptionsMonitor<AppConfig> appConfig,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        IToolChainBuilder toolChainBuilder,
        ISkillPrerequisiteResolver prerequisiteResolver,
        ISkillFileReader skillFileReader,
        ICompositeResponseSanitizer sanitizer,
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
        _sanitizer = sanitizer;
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
        var middlewareTypes = ResolveMiddlewareTypes(options);

        // What the agent is charged for up front. The same figures are recorded once below and handed to
        // the per-turn measurer as the baseline it subtracts, so the two MUST agree — which is why they
        // travel as one value rather than as three arguments spelled out twice.
        var staticBudget = new PerTurnBudgetBaseline(agentName, instruction, tools.Count);

        // The rail ends with the measurer that charges what it injects into every turn — appended inside
        // the builder and handed back read-only, so nothing out here can displace it from last
        // (issues #266, #271, #277). The rule itself is stated on AppendPerTurnBudgetProvider.
        var aiContextProviders = BuildMergedAIContextProviders(
            skills.Count,
            effectiveAllowedTools,
            disclosableSkills,
            staticBudget);

        var frameworkType = options.FrameworkType
            ?? ResolveFrameworkTypeFromMetadata(primarySkill)
            ?? _appConfig.CurrentValue.AI?.AgentFramework?.ClientType
            ?? AIAgentFrameworkClientType.AzureOpenAI;

        // Resolve or create a trace scope for this execution
        var traceScope = options.TraceScope ?? TraceScope.ForExecution(Guid.NewGuid());

        RecordStaticContextBudget(staticBudget);

        var additionalProps = BuildAdditionalProperties(primarySkill, options);

        // Compute prerequisite map for middleware consumption
        // non-null: `tools` is the result of ToList() at method start; the ?. usages elsewhere are defensive only
        var prerequisiteMap = _prerequisiteResolver.BuildPrerequisiteMap(skills, tools!);
        if (prerequisiteMap.HasAnyPrerequisites)
            additionalProps[SkillPrerequisiteMap.AdditionalPropertiesKey] = prerequisiteMap;

        await StashResilientChatClientAsync(additionalProps, agentName, frameworkType, deploymentName);
        await StartTraceRunAsync(additionalProps, agentName, traceScope);

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
    /// Records what the agent's static context costs before a single turn runs: the system prompt and
    /// the tool schemas.
    /// </summary>
    /// <param name="baseline">
    /// The figures to charge. This is the same value handed to the rail builder, deliberately: the
    /// measurer subtracts exactly what is recorded here, and taking one value rather than three
    /// arguments is what stops the two from being spelled out differently.
    /// </param>
    /// <remarks>
    /// These two are charged once, at construction, because they are the same on every turn. What the
    /// context-provider rail adds per turn is charged separately by
    /// <see cref="Services.Agent.PerTurnBudgetContextProvider"/>, which subtracts exactly these figures
    /// as its baseline so the prompt is not billed again every turn.
    /// </remarks>
    private void RecordStaticContextBudget(PerTurnBudgetBaseline baseline)
    {
        if (_budgetTracker is null)
            return;

        _budgetTracker.RecordAndPublish(
            baseline.AgentName,
            ContextConventions.BudgetComponents.SystemPrompt,
            ContextConventions.SourceTypeValues.SystemPrompt,
            TokenEstimationHelper.EstimateTokens(baseline.Instruction),
            ContextBudgetMetrics.SystemPromptTokens);

        if (baseline.ToolCount > 0)
        {
            _budgetTracker.RecordAndPublish(
                baseline.AgentName,
                ContextConventions.BudgetComponents.ToolSchemas,
                ContextConventions.SourceTypeValues.ToolsSchema,
                TokenEstimationHelper.EstimateToolSchemaTokens(baseline.ToolCount),
                ContextBudgetMetrics.ToolsSchemaTokens);
        }
    }

    /// <summary>
    /// Stashes the composed resilient chat client for <c>AgentFactory</c> to consume, when this agent is
    /// one the fallback chain may stand in for.
    /// </summary>
    /// <param name="additionalProps">The context's property bag, written to on success.</param>
    /// <param name="agentName">The agent being built; diagnostics only.</param>
    /// <param name="frameworkType">The framework this context resolved to.</param>
    /// <param name="deploymentName">The deployment this context resolved to.</param>
    /// <remarks>
    /// Two gates, and both matter. <c>ResilienceConfig.Enabled</c>, because when resilience is off the
    /// provider returns the PRIMARY raw client, which must not override the per-context resolution
    /// already made. And <see cref="ResilientClientEligibility"/>, because the fallback chain can
    /// only stand in for a context that resolved to exactly the primary configured provider and default
    /// deployment — per-skill or per-options overrides, <c>PersistentAgents</c> (which is AgentId-bound),
    /// <c>FoundryResponses</c> and <c>Echo</c> all keep their raw client.
    /// </remarks>
    private async Task StashResilientChatClientAsync(
        Dictionary<string, object> additionalProps,
        string agentName,
        AIAgentFrameworkClientType frameworkType,
        string deploymentName)
    {
        if (_resilientChatClientProvider is null
            || _appConfig.CurrentValue.AI?.Resilience?.Enabled != true)
            return;

        if (!ResilientClientEligibility.IsEligible(
                frameworkType, deploymentName, _appConfig.CurrentValue.AI?.AgentFramework))
        {
            _logger.LogDebug(
                "Resilience enabled but agent {AgentName} keeps its raw client: resolved {FrameworkType}/{Deployment} is not the primary configured provider/deployment",
                agentName, frameworkType, deploymentName);
            return;
        }

        additionalProps[IResilientChatClientProvider.AdditionalPropertiesKey] =
            await _resilientChatClientProvider.GetResilientChatClientAsync();

        _logger.LogDebug("Stashed resilient chat client (fallback chain) for agent {AgentName}", agentName);
    }

    /// <summary>
    /// Starts a trace run for this execution and stashes its writer, when a trace store is wired in.
    /// </summary>
    /// <param name="additionalProps">The context's property bag, written to on success.</param>
    /// <param name="agentName">The agent being traced.</param>
    /// <param name="traceScope">The scope this execution runs under.</param>
    /// <remarks>
    /// The candidate baggage is set on the ambient activity rather than passed anywhere, because
    /// <c>CausalSpanAttributionProcessor</c> reads it off the activity from inside the exporter pipeline
    /// — there is no call path between the two to hand it along.
    /// </remarks>
    private async Task StartTraceRunAsync(
        Dictionary<string, object> additionalProps,
        string agentName,
        TraceScope traceScope)
    {
        if (_traceStore is null)
            return;

        var metadata = new RunMetadata
        {
            AgentName = agentName,
            StartedAt = DateTimeOffset.UtcNow
        };

        additionalProps[ITraceWriter.AdditionalPropertiesKey] =
            await _traceStore.StartRunAsync(traceScope, metadata);

        if (traceScope.CandidateId.HasValue)
        {
            System.Diagnostics.Activity.Current?.AddBaggage(
                Domain.AI.Telemetry.Conventions.ToolConventions.HarnessCandidateId,
                traceScope.CandidateId.Value.ToString("D"));
        }
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
        var deploymentName = definition.ModelOverride ?? DefaultDeployment;

        var context = new AgentExecutionContext
        {
            // Through ToAgentName, which already owns this rule and is idempotent for a name that
            // already ends in "Agent". The hand-written concatenation here produced the same string
            // today only because AgentType is an enum and so is never "Agent"-suffixed — a second copy
            // of a rule, correct by coincidence.
            Name = ToAgentName(definition.AgentType.ToString()),
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
            context.Tools = _toolChainBuilder.BuildToolsByName(toolNames, context.Name);

        return context;
    }
}

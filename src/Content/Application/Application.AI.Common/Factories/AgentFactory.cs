using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Models;
using Domain.AI.Agents;
using Domain.AI.Routing.Models;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Factories;

/// <summary>
/// Central factory for creating configured AI agents with observability, caching, and middleware.
/// Supports creating agents from execution contexts, skill definitions, batch discovery,
/// and provisioning new persistent agents in Azure AI Foundry.
/// </summary>
public class AgentFactory : IAgentFactory
{
    /// <summary>
    /// Key under which the per-conversation scope identifier is expected in
    /// <see cref="AgentExecutionContext.AdditionalProperties"/>. This value scopes
    /// skill-completion tracking (<see cref="ISkillCompletionTracker"/>) so that
    /// prerequisite unlock/relock state survives the lifetime of a single conversation.
    /// The caller that builds the agent (e.g. the conversation cache) must flow the real
    /// conversation identifier in under this key whenever the agent declares skill
    /// prerequisites; otherwise the prerequisite middleware has no stable scope.
    /// </summary>
    public const string ConversationIdPropertyKey = "conversationId";

    private readonly ILogger<AgentFactory> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IDistributedCache _distributedCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISkillMetadataRegistry _skillRegistry;
    private readonly AgentExecutionContextFactory _agentContextFactory;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISkillCompletionTracker _completionTracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentFactory"/> class.
    /// </summary>
    /// <param name="logger">Logger for agent creation diagnostics.</param>
    /// <param name="appConfig">Application configuration for deployment defaults.</param>
    /// <param name="distributedCache">Distributed cache for chat client middleware.</param>
    /// <param name="loggerFactory">Logger factory for creating middleware loggers.</param>
    /// <param name="agentContextFactory">Factory for mapping skills to execution contexts.</param>
    /// <param name="skillRegistry">Registry for discovering skill metadata.</param>
    /// <param name="chatClientFactory">Factory for creating chat clients from configured providers.</param>
    /// <param name="serviceProvider">Service provider for resolving optional dependencies.</param>
    /// <param name="completionTracker">Tracks skill completion state for prerequisite enforcement.</param>
    public AgentFactory(
        ILogger<AgentFactory> logger,
        IOptionsMonitor<AppConfig> appConfig,
        IDistributedCache distributedCache,
        ILoggerFactory loggerFactory,
        AgentExecutionContextFactory agentContextFactory,
        ISkillMetadataRegistry skillRegistry,
        IChatClientFactory chatClientFactory,
        IServiceProvider serviceProvider,
        ISkillCompletionTracker completionTracker)
    {
        _logger = logger;
        _appConfig = appConfig;
        _distributedCache = distributedCache;
        _loggerFactory = loggerFactory;
        _agentContextFactory = agentContextFactory;
        _skillRegistry = skillRegistry;
        _chatClientFactory = chatClientFactory;
        _serviceProvider = serviceProvider;
        _completionTracker = completionTracker;
    }

    /// <inheritdoc />
    public bool IsProviderAvailable(AIAgentFrameworkClientType clientType)
        => _chatClientFactory.IsAvailable(clientType);

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders()
        => _chatClientFactory.GetAvailableProviders();

    /// <inheritdoc />
    public async Task<AIAgent> CreateAgentAsync(AgentExecutionContext agentContext, CancellationToken cancellationToken = default)
    {
        var clientType = agentContext.AIAgentFrameworkType;

        if (!_chatClientFactory.IsAvailable(clientType))
        {
            var available = _chatClientFactory.GetAvailableProviders()
                .Where(p => p.Value).Select(p => p.Key.ToString()).ToList();
            var availableStr = available.Count == 0 ? "none" : string.Join(", ", available);
            throw new InvalidOperationException(
                $"The '{clientType}' AI provider is not configured. Available providers: [{availableStr}]. " +
                "Set AppConfig.AI.AgentFramework (ClientType, Endpoint, ApiKey, DefaultDeployment) via appsettings.json, " +
                "user-secrets, or environment variables. For Azure AI Foundry with Claude/Anthropic, use ClientType=Anthropic.");
        }

        var deploymentOrAgentId = clientType == AIAgentFrameworkClientType.PersistentAgents
            ? agentContext.AgentId ?? throw new ArgumentException(
                "AgentId is required when using PersistentAgents framework type.", nameof(agentContext))
            : agentContext.DeploymentName
                ?? _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment
                ?? "default";

        _logger.LogInformation("Creating agent {AgentName} using {ClientType} with {Deployment}",
            agentContext.Name, clientType, deploymentOrAgentId);

        if (agentContext.Tools?.Count > 0)
        {
            _logger.LogInformation("Agent {AgentName} configured with {ToolCount} tools",
                agentContext.Name, agentContext.Tools.Count);
        }

        // Build agent options, wiring any AIContextProviders for progressive skill disclosure.
        // Shared by every provider path.
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentContext.Name,
            Description = agentContext.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = agentContext.Instruction,
                Tools = agentContext.Tools,
                Temperature = agentContext.Temperature
            },
            AIContextProviders = agentContext.AIContextProviders?.Count > 0
                ? agentContext.AIContextProviders
                : null
        };

        // The Foundry Responses provider yields an AIAgent directly (no IChatClient surface), so it
        // is built via IFoundryAgentProvider with the harness middleware injected through the
        // client-factory hook. Every other provider returns an IChatClient we wrap into a
        // ChatClientAgent. Both paths share the agent-level OpenTelemetry wrap below.
        var agent = clientType == AIAgentFrameworkClientType.FoundryResponses
            ? await CreateFoundryResponsesAgentAsync(agentContext, deploymentOrAgentId, agentOptions, cancellationToken)
            : await CreateChatClientAgentAsync(agentContext, clientType, deploymentOrAgentId, agentOptions, cancellationToken);

        // Wrap with agent-level OpenTelemetry. Sensitive-data capture is gated by the
        // configured content-capture policy (default off) — never hardcoded on.
        var captureSensitive = ShouldEnableSensitiveData(
            _serviceProvider.GetService<IContentCapturePolicy>());
        return agent.AsBuilder()
            .UseOpenTelemetry(configure: c => c.EnableSensitiveData = captureSensitive)
            .Build();
    }

    /// <summary>
    /// Builds an agent from an <see cref="IChatClient"/> provider (Azure OpenAI, OpenAI, AI
    /// Inference, Persistent Agents, Anthropic, Echo): resolves the chat client, wraps it in the
    /// harness middleware pipeline, and constructs a <see cref="ChatClientAgent"/>.
    /// </summary>
    /// <remarks>
    /// When <see cref="AgentExecutionContextFactory"/> stashed a resilient chat client in the
    /// context (only done when <c>ResilienceConfig.Enabled</c> is true AND the context resolved
    /// to the primary configured provider + default deployment), that client — which spans the
    /// configured provider fallback chain with per-provider Polly retry, circuit breaker, and
    /// timeout pipelines — replaces the raw per-provider client so live turns execute through
    /// the resilience pipelines. The consume side re-checks eligibility
    /// (<see cref="ResilientClientEligibility"/>) as defense in depth: PersistentAgents contexts
    /// keep their provisioned AgentId path, and per-context deployment/framework overrides keep
    /// their raw client even if a stash is present. Coverage note: only contexts built by
    /// <see cref="AgentExecutionContextFactory"/> (skill-built agents) ever carry a stash —
    /// callers constructing <see cref="AgentExecutionContext"/> manually (e.g. evaluation
    /// harnesses) and FoundryResponses agents do not route through the resilient client.
    /// </remarks>
    private async Task<AIAgent> CreateChatClientAgentAsync(
        AgentExecutionContext agentContext,
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        ChatClientAgentOptions agentOptions,
        CancellationToken cancellationToken)
    {
        var chatClient = ResolveStashedResilientClient(agentContext, clientType, deploymentOrAgentId)
            ?? await _chatClientFactory.GetChatClientAsync(
                clientType, deploymentOrAgentId, cancellationToken);

        var middlewareEnabledChatClient = BuildMiddlewarePipeline(chatClient, agentContext);

        return new ChatClientAgent(middlewareEnabledChatClient, agentOptions);
    }

    /// <summary>
    /// Returns the resilient chat client stashed in the execution context under
    /// <see cref="Interfaces.Resilience.IResilientChatClientProvider.AdditionalPropertiesKey"/>,
    /// or <see langword="null"/> when nothing is stashed (resilience disabled) or the context is
    /// not eligible for substitution — PersistentAgents (AgentId-bound), Echo, or a per-context
    /// framework/deployment override differing from the primary configured provider. Ineligible
    /// contexts always fall back to the raw per-provider client, even if a stash is present.
    /// </summary>
    private IChatClient? ResolveStashedResilientClient(
        AgentExecutionContext agentContext,
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId)
    {
        if (agentContext.AdditionalProperties?.TryGetValue(
                Interfaces.Resilience.IResilientChatClientProvider.AdditionalPropertiesKey,
                out var stashed) != true
            || stashed is not IChatClient resilientClient)
        {
            return null;
        }

        if (!ResilientClientEligibility.IsEligible(
                clientType, deploymentOrAgentId, _appConfig.CurrentValue.AI?.AgentFramework))
        {
            _logger.LogDebug(
                "Agent {AgentName} carries a stashed resilient client but resolved {ClientType}/{Deployment} is not eligible — using raw provider client",
                agentContext.Name, clientType, deploymentOrAgentId);
            return null;
        }

        _logger.LogInformation(
            "Agent {AgentName} using resilient chat client (provider fallback chain) instead of raw provider client",
            agentContext.Name);
        return resilientClient;
    }

    /// <summary>
    /// Builds a Foundry Responses agent (direct inference) via <see cref="IFoundryAgentProvider"/>,
    /// injecting the harness middleware pipeline through the provider's client-factory hook so the
    /// Foundry path retains the same OpenTelemetry, function-invocation, observability, prerequisite,
    /// and caching behaviour as the <see cref="IChatClient"/> providers.
    /// </summary>
    private async Task<AIAgent> CreateFoundryResponsesAgentAsync(
        AgentExecutionContext agentContext,
        string model,
        ChatClientAgentOptions agentOptions,
        CancellationToken cancellationToken)
    {
        var provider = _serviceProvider.GetService<IFoundryAgentProvider>()
            ?? throw new InvalidOperationException(
                "ClientType 'FoundryResponses' requires an IFoundryAgentProvider, which is registered " +
                "only when AppConfig:AI:AIFoundry:ProjectEndpoint is configured. Set the Foundry project " +
                "endpoint and Entra credentials, or choose a different ClientType.");

        return await provider.CreateAgentAsync(
            model,
            agentOptions,
            clientFactory: inner => BuildMiddlewarePipeline(inner, agentContext),
            cancellationToken);
    }

    /// <summary>
    /// Wraps an inner <see cref="IChatClient"/> in the harness middleware pipeline:
    /// OpenTelemetry → function invocation → observability → tool diagnostics →
    /// (optional) skill-prerequisite gating → distributed cache. Shared by every provider path,
    /// including the Foundry client-factory hook, so middleware behaviour is identical regardless of
    /// how the agent is constructed.
    /// </summary>
    private IChatClient BuildMiddlewarePipeline(IChatClient chatClient, AgentExecutionContext agentContext)
    {
        // Gate prompt/completion/tool-argument capture behind the configured content-capture
        // policy (default off). Previously hardcoded true, which exported sensitive content to
        // every trace exporter in every deployment.
        var captureSensitive = ShouldEnableSensitiveData(
            _serviceProvider.GetService<IContentCapturePolicy>());

        var chatClientBuilder = chatClient.AsBuilder()
            // OpenTelemetry MUST sit below UseFunctionInvocation: FunctionInvokingChatClient
            // resolves its ActivitySource via innerClient.GetService<ActivitySource>() (exposed
            // only by the OpenTelemetry chat client) and emits per-tool execute_tool spans solely
            // when that lookup succeeds. Composed above, the lookup returns null and no execute_tool
            // span is produced, starving the tool-effectiveness/usefulness/causal span processors
            // and their dashboard tiles.
            .UseFunctionInvocation(configure: c =>
            {
                c.AllowConcurrentInvocation = true;
                c.IncludeDetailedErrors = true;
                c.MaximumConsecutiveErrorsPerRequest = 3;
                c.MaximumIterationsPerRequest = 5;
                c.TerminateOnUnknownCalls = true;
            })
            .UseOpenTelemetry(configure: c => c.EnableSensitiveData = captureSensitive)
            .Use(inner => new Middleware.ObservabilityMiddleware(
                inner,
                _loggerFactory.CreateLogger<Middleware.ObservabilityMiddleware>()))
            .Use(inner => new Middleware.ToolDiagnosticsMiddleware(
                inner, _loggerFactory.CreateLogger<Middleware.ToolDiagnosticsMiddleware>()));

        // Per-turn context compaction — only when enabled in config AND a compaction service is
        // registered. Summarizes conversation history before the model call once its estimated
        // token footprint exceeds the configured budget. Fail-open: a compaction problem forwards
        // the untrimmed history rather than breaking the turn.
        var compactionConfig = _appConfig.CurrentValue.AI?.ContextManagement?.Compaction;
        var compactionService = _serviceProvider.GetService<IContextCompactionService>();
        if (compactionConfig?.MiddlewareEnabled == true && compactionService is not null)
        {
            chatClientBuilder = chatClientBuilder.Use(inner =>
                new Middleware.ContextCompactionMiddleware(
                    inner,
                    compactionService,
                    agentContext.Name ?? "unknown",
                    compactionConfig.MiddlewareMaxContextTokens,
                    Domain.AI.Compaction.CompactionStrategy.Full,
                    _loggerFactory.CreateLogger<Middleware.ContextCompactionMiddleware>()));
        }

        // Cache-stats enrichment — only when a generation-stats client is registered (i.e. the
        // configured provider is the OpenRouter path with prompt caching enabled). For every other
        // provider the service is absent and this middleware is skipped entirely.
        var statsClient = _serviceProvider.GetService<IGenerationStatsClient>();
        if (statsClient is not null)
        {
            var pricing = _appConfig.CurrentValue.Observability.LlmPricing;
            var pricingByModel = pricing.Models.ToDictionary(
                m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

            chatClientBuilder = chatClientBuilder.Use(inner =>
                new Middleware.CacheStatsEnrichingChatClient(
                    inner, statsClient, agentContext.Name ?? "unknown",
                    pricingByModel, pricing.DefaultModel,
                    _loggerFactory.CreateLogger<Middleware.CacheStatsEnrichingChatClient>()));
        }

        // Wire prerequisite middleware when prerequisite metadata exists
        if (agentContext.AdditionalProperties?.TryGetValue(
                SkillPrerequisiteMap.AdditionalPropertiesKey, out var prereqObj) == true
            && prereqObj is SkillPrerequisiteMap prereqMap
            && prereqMap.HasAnyPrerequisites)
        {
            var conversationId = ResolvePrerequisiteScope(agentContext);

            chatClientBuilder = chatClientBuilder.Use(inner =>
                new Middleware.SkillPrerequisiteMiddleware(
                    inner, _completionTracker, prereqMap, conversationId,
                    _loggerFactory.CreateLogger<Middleware.SkillPrerequisiteMiddleware>()));
        }

        chatClientBuilder = chatClientBuilder.UseDistributedCache(_distributedCache);

        return chatClientBuilder.Build();
    }

    /// <summary>
    /// Computes whether the OpenTelemetry chat/agent instrumentation may attach sensitive GenAI
    /// content — prompts, completions, and tool-call arguments/results — to spans. Returns
    /// <see langword="true"/> only when the configured <see cref="IContentCapturePolicy"/> permits
    /// at least one such capture; defaults to <see langword="false"/> (the secure default) when no
    /// policy is registered.
    /// </summary>
    /// <param name="policy">
    /// The content-capture policy resolved from configuration, or <see langword="null"/> when the
    /// content-capture pipeline is not wired into the container.
    /// </param>
    /// <returns>
    /// <see langword="true"/> to enable OpenTelemetry sensitive-data capture; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The single OTel <c>EnableSensitiveData</c> boolean cannot express the policy's finer-grained
    /// per-attribute toggles (prompt vs. output vs. tool arguments vs. tool result). It is therefore
    /// driven by the "is any sensitive capture enabled" decision. Enforcing each attribute
    /// independently would require a dedicated GenAI span processor that strips the disallowed
    /// attributes after the built-in instrumentation writes them — tracked as a follow-up.
    /// </remarks>
    internal static bool ShouldEnableSensitiveData(IContentCapturePolicy? policy)
        => policy is not null
           && (policy.ShouldCapturePromptContent()
               || policy.ShouldCaptureOutputContent()
               || policy.ShouldCaptureToolCallArguments()
               || policy.ShouldCaptureToolCallResult());

    /// <inheritdoc />
    public async Task<(AIAgent Agent, string AgentId)> CreatePersistentAgentAsync(
        AgentExecutionContext agentContext, CancellationToken cancellationToken = default)
    {
        var deploymentName = agentContext.DeploymentName
            ?? _appConfig.CurrentValue.AI.AgentFramework.DefaultDeployment
            ?? "gpt-4o";

        var agentName = agentContext.Name ?? "harness-agent";

        _logger.LogInformation(
            "Provisioning persistent agent {AgentName} with deployment {Deployment} in AI Foundry",
            agentName, deploymentName);

        var agentId = await _chatClientFactory.CreatePersistentAgentAsync(
            model: deploymentName,
            name: agentName,
            instructions: agentContext.Instruction,
            description: agentContext.Description,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Persistent agent provisioned: {AgentId} ({AgentName})", agentId, agentName);

        // Create a new context for the persistent agent — never mutate the caller's object
        var persistentContext = new AgentExecutionContext
        {
            Name = agentContext.Name,
            Description = agentContext.Description,
            Instruction = agentContext.Instruction,
            DeploymentName = agentContext.DeploymentName,
            Tools = agentContext.Tools,
            AIContextProviders = agentContext.AIContextProviders,
            MiddlewareTypes = agentContext.MiddlewareTypes,
            Temperature = agentContext.Temperature,
            AdditionalProperties = agentContext.AdditionalProperties,
            AgentId = agentId,
            AIAgentFrameworkType = AIAgentFrameworkClientType.PersistentAgents
        };

        var agent = await CreateAgentAsync(persistentContext, cancellationToken);

        return (agent, agentId);
    }

    /// <inheritdoc />
    public Task<AIAgent> CreateAgentFromSkillAsync(string skillId, CancellationToken cancellationToken = default)
        => CreateAgentFromSkillsAsync([skillId], new SkillAgentOptions(), cancellationToken);

    /// <inheritdoc />
    public Task<AIAgent> CreateAgentFromSkillAsync(string skillId, SkillAgentOptions options, CancellationToken cancellationToken = default)
        => CreateAgentFromSkillsAsync([skillId], options, cancellationToken);

    /// <inheritdoc />
    public async Task<AIAgent> CreateAgentFromSkillsAsync(
        IReadOnlyList<string> skillIds,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var built = await CreateAgentWithContextFromSkillsAsync(skillIds, options, cancellationToken);
        return built.Agent;
    }

    /// <inheritdoc />
    public async Task<AgentBuildResult> CreateAgentWithContextFromSkillsAsync(
        IReadOnlyList<string> skillIds,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating agent from {Count} skill(s): {SkillIds}",
            skillIds.Count, string.Join(", ", skillIds));

        // Resolve each skill id, preferring the owning agent's own nested skills (its
        // <agentDir>/skills/) over the global registry so an agent-owned skill can shadow — but never
        // pollute — the shared pool. The owned store is optional: hosts that do not discover agents
        // (for example the standalone MCP server) simply resolve everything from the global registry.
        var ownedSkills = _serviceProvider.GetService<IAgentOwnedSkillStore>();
        var skills = new List<SkillDefinition>();
        foreach (var id in skillIds)
        {
            var skill = ResolveSkill(id, options.OwningAgentId, ownedSkills)
                ?? throw new InvalidOperationException(
                    $"Skill '{id}' not found. Ensure it exists in the configured skill paths " +
                    "or the owning agent's skills/ directory.");
            skills.Add(skill);
        }

        ValidatePrerequisites(skills);

        var agentContext = await _agentContextFactory.MapToAgentContextAsync(skills, options);
        var agent = await CreateAgentAsync(agentContext, cancellationToken);

        _logger.LogInformation("Created agent {AgentName} from {Count} skill(s): {SkillIds}",
            agentContext.Name, skillIds.Count, string.Join(", ", skillIds));
        return new AgentBuildResult(agent, agentContext);
    }

    /// <summary>
    /// Resolves a skill id to its definition, checking the owning agent's own nested skills first (when
    /// an <paramref name="owningAgentId"/> and an <paramref name="ownedSkills"/> store are available)
    /// and falling back to the global registry. Returns null when neither source knows the id.
    /// </summary>
    private SkillDefinition? ResolveSkill(
        string id,
        string? owningAgentId,
        IAgentOwnedSkillStore? ownedSkills)
    {
        if (owningAgentId is not null && ownedSkills is not null)
        {
            var owned = ownedSkills.TryGet(owningAgentId, id);
            if (owned is not null)
                return owned;
        }

        return _skillRegistry.TryGet(id);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsFromSkillsAsync(
        IEnumerable<string> skillIds, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var agents = new Dictionary<string, AIAgent>();
        options ??= new SkillAgentOptions();

        foreach (var skillId in skillIds)
        {
            try
            {
                var agent = await CreateAgentFromSkillAsync(skillId, options, cancellationToken);
                agents[skillId] = agent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create agent for skill: {SkillId}", skillId);
            }
        }

        return agents;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsByCategoryAsync(
        string category, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var skills = _skillRegistry.GetByCategory(category);
        return await CreateAgentsFromSkillsAsync(skills.Select(s => s.Id), options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsByTagsAsync(
        IEnumerable<string> tags, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var skills = _skillRegistry.GetByTags(tags);
        return await CreateAgentsFromSkillsAsync(skills.Select(s => s.Id), options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IChatClient> GetRoutedChatClientAsync(
        AgentTurnContext turnContext,
        string? fallbackDeployment = null,
        CancellationToken ct = default)
    {
        var modelRouter = _serviceProvider.GetService<IModelRouter>();
        if (modelRouter is not null)
        {
            var decision = await modelRouter.RouteAgentTurnAsync(turnContext, ct);
            return decision.Client;
        }

        var deployment = fallbackDeployment
            ?? _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment
            ?? "default";
        var clientType = _appConfig.CurrentValue.AI?.AgentFramework?.ClientType
            ?? AIAgentFrameworkClientType.AzureOpenAI;
        return await _chatClientFactory.GetChatClientAsync(clientType, deployment, ct);
    }

    /// <summary>
    /// Resolves the conversation scope used to key per-conversation skill-completion tracking
    /// for the prerequisite middleware.
    /// </summary>
    /// <param name="agentContext">The execution context whose additional properties carry the scope.</param>
    /// <returns>The non-empty conversation identifier supplied by the caller.</returns>
    /// <remarks>
    /// The prerequisite middleware records skill completions against this scope. The scope MUST be a
    /// stable conversation identifier supplied by the caller via
    /// <see cref="AgentExecutionContext.AdditionalProperties"/>[<see cref="ConversationIdPropertyKey"/>].
    /// A synthetic per-build identifier is deliberately NOT generated here: it would silently reset
    /// unlock state every time the cached agent is rebuilt (e.g. on sliding-expiration eviction) and
    /// would leak tracker entries keyed by throwaway identifiers that no eviction path can ever clear.
    /// Missing wiring is therefore treated as a construction-time error and surfaced loudly — matching
    /// how this factory already rejects every other construction-time misconfiguration — rather than
    /// degrading the prerequisite-gating feature into a subtly-broken state.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no non-empty conversation scope is present in the context's additional properties.
    /// </exception>
    private static string ResolvePrerequisiteScope(AgentExecutionContext agentContext)
    {
        if (agentContext.AdditionalProperties is not null
            && agentContext.AdditionalProperties.TryGetValue(ConversationIdPropertyKey, out var convId)
            && convId?.ToString() is { Length: > 0 } scope
            && !string.IsNullOrWhiteSpace(scope))
        {
            return scope;
        }

        throw new InvalidOperationException(
            $"Agent '{agentContext.Name}' declares skill prerequisites but no conversation scope was " +
            $"supplied in AgentExecutionContext.AdditionalProperties[\"{ConversationIdPropertyKey}\"]. " +
            "The caller that builds the agent must flow the real conversation identifier in under that " +
            "key (e.g. via SkillAgentOptions.AdditionalProperties) so that prerequisite completion state " +
            "is scoped to the conversation and can be cleared when the conversation is evicted. A " +
            "synthetic identifier is not generated here because it would silently reset unlocked skills " +
            "whenever the cached agent is rebuilt and leak unclearable tracker entries.");
    }

    /// <summary>
    /// Validates that all prerequisite references are valid and contain no cycles.
    /// Uses Kahn's algorithm for topological sort — if the sort doesn't include all skills,
    /// a cycle exists.
    /// </summary>
    private static void ValidatePrerequisites(IReadOnlyList<SkillDefinition> skills)
    {
        // Skip validation when no prerequisites exist
        if (!skills.Any(s => s.HasPrerequisites))
            return;

        var skillIds = new HashSet<string>(skills.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        // Check all referenced prerequisites exist in the skill list
        foreach (var skill in skills)
        {
            foreach (var prereq in skill.Prerequisites)
            {
                if (!skillIds.Contains(prereq))
                    throw new InvalidOperationException(
                        $"Skill '{skill.Id}' declares prerequisite '{prereq}' which is not in the agent's skill list. " +
                        $"Available skills: [{string.Join(", ", skillIds)}]");
            }
        }

        // Topological sort to detect cycles (Kahn's algorithm)
        var inDegree = skills.ToDictionary(s => s.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var adj = skills.ToDictionary(s => s.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var skill in skills)
        {
            foreach (var prereq in skill.Prerequisites)
            {
                adj[prereq].Add(skill.Id);
                inDegree[skill.Id]++;
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted++;
            foreach (var dependent in adj[current])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted != skills.Count)
        {
            var cycleSkills = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key);
            throw new InvalidOperationException(
                $"Prerequisite cycle detected among skills: [{string.Join(", ", cycleSkills)}]. " +
                "Remove or restructure prerequisites to eliminate the cycle.");
        }
    }
}

using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Skills;
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

        var chatClient = await _chatClientFactory.GetChatClientAsync(
            clientType, deploymentOrAgentId, cancellationToken);

        // Build ChatClient middleware pipeline
        var chatClientBuilder = chatClient.AsBuilder()
            .UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)
            .UseFunctionInvocation(configure: c =>
            {
                c.AllowConcurrentInvocation = true;
                c.IncludeDetailedErrors = true;
                c.MaximumConsecutiveErrorsPerRequest = 3;
                c.MaximumIterationsPerRequest = 5;
                c.TerminateOnUnknownCalls = true;
            })
            .Use(inner => new Middleware.ObservabilityMiddleware(
                inner,
                _loggerFactory.CreateLogger<Middleware.ObservabilityMiddleware>()))
            .Use(inner => new Middleware.ToolDiagnosticsMiddleware(
                inner, _loggerFactory.CreateLogger<Middleware.ToolDiagnosticsMiddleware>()));

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

        var middlewareEnabledChatClient = chatClientBuilder.Build();

        if (agentContext.Tools?.Count > 0)
        {
            _logger.LogInformation("Agent {AgentName} configured with {ToolCount} tools",
                agentContext.Name, agentContext.Tools.Count);
        }

        // Build agent options, wiring any AIContextProviders for progressive skill disclosure
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

        var agent = new ChatClientAgent(middlewareEnabledChatClient, agentOptions);

        // Wrap with agent-level OpenTelemetry (sensitive data off at this level)
        return agent.AsBuilder()
            .UseOpenTelemetry(configure: c => c.EnableSensitiveData = false)
            .Build();
    }

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

        var skills = new List<SkillDefinition>();
        foreach (var id in skillIds)
        {
            var skill = _skillRegistry.TryGet(id)
                ?? throw new InvalidOperationException(
                    $"Skill '{id}' not found. Ensure it exists in the configured skill paths.");
            skills.Add(skill);
        }

        ValidatePrerequisites(skills);

        var agentContext = await _agentContextFactory.MapToAgentContextAsync(skills, options);
        var agent = await CreateAgentAsync(agentContext, cancellationToken);

        _logger.LogInformation("Created agent {AgentName} from {Count} skill(s): {SkillIds}",
            agentContext.Name, skillIds.Count, string.Join(", ", skillIds));
        return new AgentBuildResult(agent, agentContext);
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

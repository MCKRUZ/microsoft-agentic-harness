using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
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
    private readonly ILogger<AgentFactory> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IDistributedCache _distributedCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISkillLoaderService _skillLoaderService;
    private readonly AgentExecutionContextFactory _agentContextFactory;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentFactory"/> class.
    /// </summary>
    /// <param name="logger">Logger for agent creation diagnostics.</param>
    /// <param name="appConfig">Application configuration for deployment defaults.</param>
    /// <param name="distributedCache">Distributed cache for chat client middleware.</param>
    /// <param name="loggerFactory">Logger factory for creating middleware loggers.</param>
    /// <param name="agentContextFactory">Factory for mapping skills to execution contexts.</param>
    /// <param name="skillLoaderService">Service for loading skill definitions and resources.</param>
    /// <param name="chatClientFactory">Factory for creating chat clients from configured providers.</param>
    /// <param name="serviceProvider">Service provider for resolving optional dependencies.</param>
    public AgentFactory(
        ILogger<AgentFactory> logger,
        IOptionsMonitor<AppConfig> appConfig,
        IDistributedCache distributedCache,
        ILoggerFactory loggerFactory,
        AgentExecutionContextFactory agentContextFactory,
        ISkillLoaderService skillLoaderService,
        IChatClientFactory chatClientFactory,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _appConfig = appConfig;
        _distributedCache = distributedCache;
        _loggerFactory = loggerFactory;
        _agentContextFactory = agentContextFactory;
        _skillLoaderService = skillLoaderService;
        _chatClientFactory = chatClientFactory;
        _serviceProvider = serviceProvider;
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
            throw new InvalidOperationException(
                $"The {clientType} provider is not configured. Check AppConfig.AI.AgentFramework settings.");
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
            .Use(inner => new Middleware.ToolDiagnosticsMiddleware(
                inner, _loggerFactory.CreateLogger<Middleware.ToolDiagnosticsMiddleware>()))
            .UseDistributedCache(_distributedCache);

        var middlewareEnabledChatClient = chatClientBuilder.Build();

        if (agentContext.Tools?.Count > 0)
        {
            _logger.LogInformation("Agent {AgentName} configured with {ToolCount} tools",
                agentContext.Name, agentContext.Tools.Count);
        }

        // Create the agent
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentContext.Name,
            Description = agentContext.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = agentContext.Instruction,
                Tools = agentContext.Tools
            }
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
            MiddlewareTypes = agentContext.MiddlewareTypes,
            AdditionalProperties = agentContext.AdditionalProperties,
            AgentId = agentId,
            AIAgentFrameworkType = AIAgentFrameworkClientType.PersistentAgents
        };

        var agent = await CreateAgentAsync(persistentContext, cancellationToken);

        return (agent, agentId);
    }

    /// <inheritdoc />
    public Task<AIAgent> CreateAgentFromSkillAsync(string skillId, CancellationToken cancellationToken = default)
        => CreateAgentFromSkillAsync(skillId, new SkillAgentOptions(), cancellationToken);

    /// <inheritdoc />
    public async Task<AIAgent> CreateAgentFromSkillAsync(string skillId, SkillAgentOptions options, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating agent from skill: {SkillId}", skillId);

        var skill = await _skillLoaderService.LoadSkillAsync(skillId, cancellationToken);
        await LoadResourcesAsync(skill, options, cancellationToken);

        var agentContext = await _agentContextFactory.MapToAgentContextAsync(skill, options);
        var agent = await CreateAgentAsync(agentContext, cancellationToken);

        _logger.LogInformation("Created agent {AgentName} from skill {SkillId}", agentContext.Name, skillId);
        return agent;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsByCategoryAsync(
        string category, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var skills = await _skillLoaderService.DiscoverByCategoryAsync(category, cancellationToken);
        return await CreateAgentsFromSkillsAsync(skills, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsByTagsAsync(
        IEnumerable<string> tags, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var skills = await _skillLoaderService.DiscoverByTagsAsync(tags, cancellationToken);
        return await CreateAgentsFromSkillsAsync(skills, options, cancellationToken);
    }

    private async Task<IDictionary<string, AIAgent>> CreateAgentsFromSkillsAsync(
        IReadOnlyList<SkillDefinition> skills, SkillAgentOptions? options, CancellationToken cancellationToken)
    {
        var agents = new Dictionary<string, AIAgent>();
        options ??= new SkillAgentOptions();

        foreach (var skill in skills)
        {
            try
            {
                var agent = await CreateAgentFromSkillAsync(skill.Id, options, cancellationToken);
                agents[skill.Id] = agent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create agent for skill: {SkillId}", skill.Id);
            }
        }

        return agents;
    }

    private async Task LoadResourcesAsync(SkillDefinition skill, SkillAgentOptions options, CancellationToken cancellationToken)
    {
        if (options.LoadAllTemplates && skill.HasTemplates)
        {
            var templates = await _skillLoaderService.LoadAllTemplatesAsync(skill.Id, cancellationToken);
            foreach (var template in skill.Templates)
            {
                if (templates.TryGetValue(template.FileName, out var content))
                    template.Content = content;
            }
        }

        if (options.TemplatesToLoad?.Count > 0)
        {
            foreach (var name in options.TemplatesToLoad)
            {
                try
                {
                    var content = await _skillLoaderService.LoadTemplateAsync(skill.Id, name, cancellationToken);
                    var template = skill.Templates.FirstOrDefault(t =>
                        t.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (template != null)
                        template.Content = content;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load template {Template} for skill {SkillId}", name, skill.Id);
                }
            }
        }

        if (options.LoadAllReferences && skill.HasReferences)
        {
            var references = await _skillLoaderService.LoadAllReferencesAsync(skill.Id, cancellationToken);
            foreach (var reference in skill.References)
            {
                if (references.TryGetValue(reference.FileName, out var content))
                    reference.Content = content;
            }
        }

        if (options.ReferencesToLoad?.Count > 0)
        {
            foreach (var name in options.ReferencesToLoad)
            {
                try
                {
                    var content = await _skillLoaderService.LoadReferenceAsync(skill.Id, name, cancellationToken);
                    var reference = skill.References.FirstOrDefault(r =>
                        r.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (reference != null)
                        reference.Content = content;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load reference {Reference} for skill {SkillId}", name, skill.Id);
                }
            }
        }
    }
}

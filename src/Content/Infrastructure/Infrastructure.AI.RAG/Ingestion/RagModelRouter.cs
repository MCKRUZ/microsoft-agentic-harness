using System.Collections.Concurrent;
using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Routes RAG pipeline operations to appropriate LLM deployments based on model tier
/// configuration. Maps operation names (e.g., <c>"raptor_summarization"</c>,
/// <c>"contextual_enrichment"</c>) to tier definitions, then uses
/// <see cref="IChatClientFactory"/> to construct the <see cref="IChatClient"/> for
/// the tier's deployment. Unknown operations fall back to the default tier with a
/// warning log rather than throwing.
/// </summary>
public sealed class RagModelRouter : IRagModelRouter
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");

    private readonly ConcurrentDictionary<string, IChatClient> _clientCache = new();
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<RagModelRouter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RagModelRouter"/> class.
    /// </summary>
    /// <param name="chatClientFactory">Factory for creating chat clients by deployment name.</param>
    /// <param name="appConfig">Application configuration providing model tiering settings.</param>
    /// <param name="logger">Logger for recording tier resolution decisions.</param>
    public RagModelRouter(
        IChatClientFactory chatClientFactory,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<RagModelRouter> logger)
    {
        _chatClientFactory = chatClientFactory;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public string GetTierForOperation(string operationName)
    {
        var tiering = _appConfig.CurrentValue.AI.Rag.ModelTiering;

        if (!tiering.Enabled)
            return tiering.DefaultTier;

        if (tiering.OperationOverrides.TryGetValue(operationName, out var overrideTier))
            return overrideTier;

        _logger.LogDebug(
            "No tier override for operation '{Operation}'; using default tier '{DefaultTier}'",
            operationName, tiering.DefaultTier);

        return tiering.DefaultTier;
    }

    /// <inheritdoc />
    public IChatClient GetClientForOperation(string operationName)
    {
        using var activity = ActivitySource.StartActivity("rag.model_router.resolve");
        activity?.SetTag(RagConventions.ModelOperation, operationName);

        var tierName = GetTierForOperation(operationName);
        activity?.SetTag(RagConventions.ModelTier, tierName);

        var tiering = _appConfig.CurrentValue.AI.Rag.ModelTiering;
        var tierDef = FindTierDefinition(tiering, tierName);
        var deploymentName = tierDef.DeploymentName;

        activity?.SetTag(RagConventions.ModelDeployment, deploymentName);

        _logger.LogDebug(
            "Routing operation '{Operation}' to tier '{Tier}' (deployment: {Deployment})",
            operationName, tierName, deploymentName);

        return _clientCache.GetOrAdd(deploymentName, name =>
            _chatClientFactory
                .GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, name)
                .GetAwaiter()
                .GetResult());
    }

    /// <summary>
    /// Finds the <see cref="Domain.Common.Config.AI.RAG.ModelTierDefinition"/> matching
    /// the given tier name. Falls back to the first available tier with a warning if
    /// no exact match is found.
    /// </summary>
    private Domain.Common.Config.AI.RAG.ModelTierDefinition FindTierDefinition(
        Domain.Common.Config.AI.RAG.ModelTieringConfig tiering,
        string tierName)
    {
        var tierDef = tiering.Tiers.FirstOrDefault(
            t => t.Name.Equals(tierName, StringComparison.OrdinalIgnoreCase));

        if (tierDef is not null)
            return tierDef;

        _logger.LogWarning(
            "Tier '{TierName}' not found in configuration. Available tiers: [{Tiers}]. " +
            "Falling back to first available tier.",
            tierName, string.Join(", ", tiering.Tiers.Select(t => t.Name)));

        return tiering.Tiers.Length > 0
            ? tiering.Tiers[0]
            : new Domain.Common.Config.AI.RAG.ModelTierDefinition
            {
                Name = "fallback",
                DeploymentName = "gpt-4o-mini"
            };
    }
}

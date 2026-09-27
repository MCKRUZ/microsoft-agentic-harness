using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Auth;
using Presentation.Common.Configuration;

namespace Presentation.AgentHub.HealthChecks;

/// <summary>
/// Reports which major subsystems this host has composed and how they are configured — issue
/// #591's "health endpoint listing composed services" for a self-hosted, non-Azure deployment.
/// </summary>
/// <remarks>
/// Names, enum values, and booleans only — never a connection string, file path, or endpoint (see
/// <see cref="AiProviderHealthCheck"/> and <c>ConfigController</c> for the same convention). Reports
/// <see cref="HealthStatus.Degraded"/>, never <see cref="HealthStatus.Unhealthy"/>: nothing this
/// check inspects is a boot failure by itself, only information an operator needs.
/// </remarks>
public sealed class ComposedSubsystemsHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly HarnessConfigSourceReport _configSourceReport;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    /// <summary>Initializes the check with the collaborators it reads subsystem state from.</summary>
    public ComposedSubsystemsHealthCheck(
        IOptionsMonitor<AppConfig> appConfig,
        IChatClientFactory chatClientFactory,
        HarnessConfigSourceReport configSourceReport,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _appConfig = appConfig;
        _chatClientFactory = chatClientFactory;
        _configSourceReport = configSourceReport;
        _environment = environment;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var config = _appConfig.CurrentValue;
        var providerStatus = _chatClientFactory.GetProviderStatus();

        var data = new Dictionary<string, object>
        {
            ["aiProvider"] = providerStatus.ClientType.ToString(),
            ["aiProviderConfigured"] = providerStatus.IsConfigured,
            ["promptCaching"] = config.AI.AgentFramework.EnablePromptCaching,
            ["cacheBackend"] = config.Cache.CacheType.ToString(),
            ["redisConfigured"] = !string.IsNullOrWhiteSpace(config.Cache.RedisClient.Endpoint),
            ["conversationStore"] = config.AI.Conversations.Provider.ToString(),
            ["knowledgeGraphEnabled"] = config.AI.Rag.GraphRag.Enabled,
            ["knowledgeGraphProvider"] = config.AI.Rag.GraphRag.GraphProvider,
            ["vectorStoreProvider"] = config.AI.Rag.VectorStore.Provider,
            ["sandboxEnabled"] = config.AI.Sandbox.Enabled,
            ["sandboxIsolationLevel"] = config.AI.Sandbox.DefaultIsolationLevel,
            ["governanceEnabled"] = config.AI.Governance.Enabled,
            ["otlpEnabled"] = config.Observability.Exporters.Otlp.Enabled,
            ["azureMonitorEnabled"] = config.Observability.Exporters.AzureMonitor.Enabled,
            ["authMode"] = AuthBypassPolicy.IsBypassed(_environment, _configuration) ? "disabled" : "entra",
            ["azureKeyVaultConfigSourceLoaded"] = _configSourceReport.AzureKeyVaultLoaded,
            ["azureAppConfigurationSourceLoaded"] = _configSourceReport.AzureAppConfigurationLoaded,
        };

        return Task.FromResult(HealthCheckResult.Healthy("Composed subsystems reported.", data));
    }
}

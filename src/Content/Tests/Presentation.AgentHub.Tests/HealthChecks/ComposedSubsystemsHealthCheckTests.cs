using Application.AI.Common.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.Cache;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.HealthChecks;
using Presentation.AgentHub.Tests.Fakes;
using Presentation.Common.Configuration;
using Xunit;

namespace Presentation.AgentHub.Tests.HealthChecks;

public sealed class ComposedSubsystemsHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReportsConfiguredSubsystems_WithoutSecrets()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { EnablePromptCaching = true },
            },
            Cache = new CacheConfig { CacheType = CacheType.RedisCache },
        };
        appConfig.AI.Rag.GraphRag.Enabled = true;
        appConfig.AI.Rag.GraphRag.GraphProvider = "neo4j";
        appConfig.Cache.RedisClient.Endpoint = "redis:6379";

        var configMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var chatClientFactory = new FakeChatClientFactory
        {
            ProviderStatus = new AiProviderStatus(
                AIAgentFrameworkClientType.OpenAI, "gpt-4o", IsConfigured: true, MissingSettings: []),
        };
        var configSourceReport = new HarnessConfigSourceReport(
            ["EnvironmentVariablesConfigurationProvider"], AzureKeyVaultLoaded: false, AzureAppConfigurationLoaded: false);
        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Disabled"] = "true",
                ["Auth:AllowOutsideDevelopment"] = "true",
            })
            .Build();

        var check = new ComposedSubsystemsHealthCheck(
            configMonitor, chatClientFactory, configSourceReport, environment, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["aiProvider"].Should().Be("OpenAI");
        result.Data["cacheBackend"].Should().Be("RedisCache");
        result.Data["redisConfigured"].Should().Be(true);
        result.Data["knowledgeGraphEnabled"].Should().Be(true);
        result.Data["knowledgeGraphProvider"].Should().Be("neo4j");
        result.Data["authMode"].Should().Be("disabled");
        result.Data["azureKeyVaultConfigSourceLoaded"].Should().Be(false);
        result.Data["azureAppConfigurationSourceLoaded"].Should().Be(false);

        // Every value must be a name, enum, or boolean — never a secret or connection detail.
        result.Data.Values.OfType<string>().Should().NotContain(v => v.Contains("redis:6379"));
    }
}

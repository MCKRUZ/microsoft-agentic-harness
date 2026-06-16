using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DriftDetection;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Learnings;
using Domain.Common.Config.AI.Resilience;
using Domain.Common.Config.Cache;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Integration tests for <see cref="IServiceCollectionExtensions"/> covering
/// config section registration, cache configuration, and auth dependency wiring.
/// </summary>
public sealed class IServiceCollectionExtensionsTests
{
    // -- RegisterConfigSections --

    [Fact]
    public void RegisterConfigSections_BindsAppConfigSection()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:Common:ApplicationName"] = "TestApp"
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var appConfig = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppConfig>>().Value;
        appConfig.Common.ApplicationName.Should().Be("TestApp");
    }

    [Fact]
    public void RegisterConfigSections_BindsAllSubsections()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:Common:ApplicationVersion"] = "2.0",
                ["AppConfig:Logging:PipeName"] = "test-pipe",
                ["AppConfig:Cache:CacheType"] = "Memory"
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var commonConfig = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Domain.Common.Config.CommonConfig>>().Value;
        commonConfig.ApplicationVersion.Should().Be("2.0");

        var loggingConfig = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Domain.Common.Config.LoggingConfig>>().Value;
        loggingConfig.PipeName.Should().Be("test-pipe");
    }

    [Fact]
    public void RegisterConfigSections_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        var result = services.RegisterConfigSections(config);

        result.Should().BeSameAs(services);
    }

    // -- AddCacheConfiguration --

    [Fact]
    public void AddCacheConfiguration_MemoryCache_RegistersMemoryAndDistributed()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig { CacheType = CacheType.Memory };

        services.AddCacheConfiguration(cacheConfig);
        var provider = services.BuildServiceProvider();

        provider.GetService<IMemoryCache>().Should().NotBeNull();
        provider.GetService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void AddCacheConfiguration_NoneCache_RegistersMemoryAndDistributed()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig { CacheType = CacheType.None };

        services.AddCacheConfiguration(cacheConfig);
        var provider = services.BuildServiceProvider();

        provider.GetService<IMemoryCache>().Should().NotBeNull();
        provider.GetService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void AddCacheConfiguration_DistributedMemory_RegistersDistributedCache()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig { CacheType = CacheType.DistributedMemory };

        services.AddCacheConfiguration(cacheConfig);
        var provider = services.BuildServiceProvider();

        provider.GetService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void AddCacheConfiguration_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig();

        var result = services.AddCacheConfiguration(cacheConfig);

        result.Should().BeSameAs(services);
    }

    // -- AddAuthDependencies --

    [Fact]
    public void AddAuthDependencies_NoB2CInstance_RegistersBasicAuth()
    {
        var services = new ServiceCollection();
        var azureConfig = new Domain.Common.Config.Azure.AzureConfig();

        services.AddAuthDependencies(azureConfig);

        services.Any(d => d.ServiceType.Name.Contains("Authorization")).Should().BeTrue();
        services.Any(d => d.ServiceType.Name.Contains("Authentication")).Should().BeTrue();
    }

    [Fact]
    public void AddAuthDependencies_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var azureConfig = new Domain.Common.Config.Azure.AzureConfig();

        var result = services.AddAuthDependencies(azureConfig);

        result.Should().BeSameAs(services);
    }

    // -- EscalationConfig and ResilienceConfig bindings --

    [Fact]
    public void RegisterConfigSections_BindsEscalationConfig()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Governance:Escalation:Enabled"] = "true",
                ["AppConfig:AI:Governance:Escalation:DefaultTimeoutSeconds"] = "600",
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var escConfig = provider.GetRequiredService<IOptionsMonitor<EscalationConfig>>().CurrentValue;
        escConfig.Enabled.Should().BeTrue();
        escConfig.DefaultTimeoutSeconds.Should().Be(600);
    }

    [Fact]
    public void RegisterConfigSections_BindsResilienceConfig()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Resilience:Enabled"] = "true",
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var resConfig = provider.GetRequiredService<IOptionsMonitor<ResilienceConfig>>().CurrentValue;
        resConfig.Enabled.Should().BeTrue();
    }

    [Fact]
    public void EscalationConfig_BindsFullStructure_FromAppsettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Governance:Escalation:Enabled"] = "true",
                ["AppConfig:AI:Governance:Escalation:DefaultTimeoutSeconds"] = "300",
                ["AppConfig:AI:Governance:Escalation:DefaultTimeoutAction"] = "DenyAndEscalate",
                ["AppConfig:AI:Governance:Escalation:DefaultApprovalStrategy"] = "AnyOf",
                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Informational:TimeoutSeconds"] = "0",
                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Informational:Async"] = "true",
                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Blocking:TimeoutSeconds"] = "300",
                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Blocking:Async"] = "false",
                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Critical:TimeoutSeconds"] = "600",
                ["AppConfig:AI:Governance:Escalation:PriorityLevels:Critical:EscalateToAll"] = "true",
            })
            .Build();

        var escConfig = config.GetSection("AppConfig:AI:Governance:Escalation").Get<EscalationConfig>();

        escConfig.Should().NotBeNull();
        escConfig!.Enabled.Should().BeTrue();
        escConfig.DefaultTimeoutSeconds.Should().Be(300);
        escConfig.DefaultTimeoutAction.Should().Be("DenyAndEscalate");
        escConfig.DefaultApprovalStrategy.Should().Be("AnyOf");
        escConfig.PriorityLevels.Should().ContainKeys("Informational", "Blocking", "Critical");
        escConfig.PriorityLevels["Informational"].Async.Should().BeTrue();
        escConfig.PriorityLevels["Critical"].EscalateToAll.Should().BeTrue();
        escConfig.PriorityLevels["Critical"].TimeoutSeconds.Should().Be(600);
    }

    [Fact]
    public void ResilienceConfig_BindsFullStructure_FromAppsettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Resilience:Enabled"] = "false",
                ["AppConfig:AI:Resilience:FallbackChain:0:ClientType"] = "AzureOpenAI",
                ["AppConfig:AI:Resilience:FallbackChain:0:DeploymentId"] = "gpt-4o",
                ["AppConfig:AI:Resilience:FallbackChain:0:Capabilities:SupportsToolCalling"] = "true",
                ["AppConfig:AI:Resilience:FallbackChain:1:ClientType"] = "AzureAIInference",
                ["AppConfig:AI:Resilience:FallbackChain:1:DeploymentId"] = "claude-sonnet",
                ["AppConfig:AI:Resilience:CircuitBreaker:FailureRatio"] = "0.5",
                ["AppConfig:AI:Resilience:Retry:MaxAttempts"] = "2",
                ["AppConfig:AI:Resilience:Timeout:PerAttemptSeconds"] = "30",
                ["AppConfig:AI:Resilience:DegradedMode:RetryQueueTtlSeconds"] = "300",
                ["AppConfig:AI:Resilience:DegradedMode:MaxQueueSize"] = "100",
            })
            .Build();

        var resConfig = config.GetSection("AppConfig:AI:Resilience").Get<ResilienceConfig>();

        resConfig.Should().NotBeNull();
        resConfig!.Enabled.Should().BeFalse();
        resConfig.FallbackChain.Should().HaveCount(2);
        resConfig.FallbackChain[0].ClientType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
        resConfig.FallbackChain[0].DeploymentId.Should().Be("gpt-4o");
        resConfig.FallbackChain[1].ClientType.Should().Be(AIAgentFrameworkClientType.AzureAIInference);
        resConfig.CircuitBreaker.FailureRatio.Should().Be(0.5);
        resConfig.Retry.MaxAttempts.Should().Be(2);
        resConfig.Timeout.PerAttemptSeconds.Should().Be(30);
        resConfig.DegradedMode.RetryQueueTtlSeconds.Should().Be(300);
        resConfig.DegradedMode.MaxQueueSize.Should().Be(100);
    }

    // -- DriftDetectionConfig and LearningsConfig bindings --

    [Fact]
    public void DriftDetectionConfig_BindsFromAppsettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:DriftDetection:Enabled"] = "true",
                ["AppConfig:AI:DriftDetection:EwmaLambda"] = "0.2",
                ["AppConfig:AI:DriftDetection:ControlLimitWidth"] = "3.0",
                ["AppConfig:AI:DriftDetection:MinSamplesForBaseline"] = "20",
                ["AppConfig:AI:DriftDetection:BaselineWindowDays"] = "7",
                ["AppConfig:AI:DriftDetection:WarnThresholdSigma"] = "1.5",
                ["AppConfig:AI:DriftDetection:AlertThresholdSigma"] = "2.5",
                ["AppConfig:AI:DriftDetection:EscalateThresholdSigma"] = "3.0",
                ["AppConfig:AI:DriftDetection:EscalationEnabled"] = "true",
                ["AppConfig:AI:DriftDetection:AuditPath"] = "data/audit",
            })
            .Build();

        var driftConfig = config.GetSection("AppConfig:AI:DriftDetection").Get<DriftDetectionConfig>();

        driftConfig.Should().NotBeNull();
        driftConfig!.Enabled.Should().BeTrue();
        driftConfig.EwmaLambda.Should().Be(0.2);
        driftConfig.ControlLimitWidth.Should().Be(3.0);
        driftConfig.MinSamplesForBaseline.Should().Be(20);
        driftConfig.BaselineWindowDays.Should().Be(7);
        driftConfig.WarnThresholdSigma.Should().Be(1.5);
        driftConfig.AlertThresholdSigma.Should().Be(2.5);
        driftConfig.EscalateThresholdSigma.Should().Be(3.0);
        driftConfig.EscalationEnabled.Should().BeTrue();
        driftConfig.AuditPath.Should().Be("data/audit");
    }

    [Fact]
    public void LearningsConfig_BindsFromAppsettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Learnings:Enabled"] = "true",
                ["AppConfig:AI:Learnings:StoreProvider"] = "graph",
                ["AppConfig:AI:Learnings:FeedbackAlpha"] = "0.25",
                ["AppConfig:AI:Learnings:FeedbackCeiling"] = "0.3",
                ["AppConfig:AI:Learnings:DiversityInjectionRatio"] = "0.15",
                ["AppConfig:AI:Learnings:VolatileShelfLifeDays"] = "7",
                ["AppConfig:AI:Learnings:StableShelfLifeDays"] = "180",
                ["AppConfig:AI:Learnings:PruneIntervalHours"] = "24",
                ["AppConfig:AI:Learnings:BaselineAdjustmentThreshold"] = "0.8",
                ["AppConfig:AI:Learnings:BiasCorrection"] = "true",
                ["AppConfig:AI:Learnings:DecayBiasAlpha"] = "0.25",
            })
            .Build();

        var learningsConfig = config.GetSection("AppConfig:AI:Learnings").Get<LearningsConfig>();

        learningsConfig.Should().NotBeNull();
        learningsConfig!.Enabled.Should().BeTrue();
        learningsConfig.StoreProvider.Should().Be("graph");
        learningsConfig.FeedbackAlpha.Should().Be(0.25);
        learningsConfig.FeedbackCeiling.Should().Be(0.3);
        learningsConfig.DiversityInjectionRatio.Should().Be(0.15);
        learningsConfig.VolatileShelfLifeDays.Should().Be(7);
        learningsConfig.StableShelfLifeDays.Should().Be(180);
        learningsConfig.PruneIntervalHours.Should().Be(24);
        learningsConfig.BaselineAdjustmentThreshold.Should().Be(0.8);
        learningsConfig.BiasCorrection.Should().BeTrue();
        learningsConfig.DecayBiasAlpha.Should().Be(0.25);
    }

    [Fact]
    public void RegisterConfigSections_BindsDriftDetectionConfig()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:DriftDetection:Enabled"] = "true",
                ["AppConfig:AI:DriftDetection:EwmaLambda"] = "0.2",
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var driftConfig = provider.GetRequiredService<IOptionsMonitor<DriftDetectionConfig>>().CurrentValue;
        driftConfig.Enabled.Should().BeTrue();
        driftConfig.EwmaLambda.Should().Be(0.2);
    }

    [Fact]
    public void RegisterConfigSections_BindsLearningsConfig()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Learnings:Enabled"] = "true",
                ["AppConfig:AI:Learnings:FeedbackAlpha"] = "0.25",
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var learningsConfig = provider.GetRequiredService<IOptionsMonitor<LearningsConfig>>().CurrentValue;
        learningsConfig.Enabled.Should().BeTrue();
        learningsConfig.FeedbackAlpha.Should().Be(0.25);
    }

    // -- SandboxConfig (capability enforcement) binding (regression for finding 0) --

    [Fact]
    public void RegisterConfigSections_BindsSandboxConfig_FromSandboxCapabilitiesSection()
    {
        // Arrange: operator overrides the least-privilege defaults under the
        // AppConfig:AI:SandboxCapabilities path (distinct from AppConfig:AI:Sandbox,
        // which binds the unrelated SandboxOptions class).
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:SandboxCapabilities:Enabled"] = "false",
                ["AppConfig:AI:SandboxCapabilities:WorkspaceRoot"] = "/srv/sandbox",
                ["AppConfig:AI:SandboxCapabilities:DefaultGrantedCapabilities:0"] = "FileRead",
                ["AppConfig:AI:SandboxCapabilities:DefaultGrantedCapabilities:1"] = "FileWrite",
            })
            .Build();

        // Act
        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        // Assert: before the fix this resolved compiled defaults only (Enabled=true,
        // WorkspaceRoot=null, [FileRead, LlmInvocation]) regardless of configuration.
        var sandboxConfig = provider
            .GetRequiredService<IOptionsMonitor<Domain.Common.Config.AI.Sandbox.SandboxConfig>>()
            .CurrentValue;
        sandboxConfig.Enabled.Should().BeFalse();
        sandboxConfig.WorkspaceRoot.Should().Be("/srv/sandbox");
        sandboxConfig.DefaultGrantedCapabilities.Should().Contain("FileWrite");
    }

    [Fact]
    public void RegisterConfigSections_SandboxConfig_DefaultsWhenSectionAbsent()
    {
        // Arrange: no SandboxCapabilities section configured.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        // Act
        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        // Assert: least-privilege compiled defaults survive when nothing overrides them.
        var sandboxConfig = provider
            .GetRequiredService<IOptionsMonitor<Domain.Common.Config.AI.Sandbox.SandboxConfig>>()
            .CurrentValue;
        sandboxConfig.Enabled.Should().BeTrue();
        sandboxConfig.DefaultGrantedCapabilities.Should().BeEquivalentTo("FileRead", "LlmInvocation");
    }

    // -- AppConfig tree exposes PromptUsage + SandboxCapabilities (findings 0 & 49) --

    [Fact]
    public void AIConfig_BindsPromptUsageOptions_FromAppConfigTree()
    {
        // Arrange: PromptUsageOptions is forwarded into AddPromptRegistry from the
        // AppConfig tree, so PersistenceEnabled must be reachable through AppConfig:AI:PromptUsage.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:PromptUsage:PersistenceEnabled"] = "true",
                ["AppConfig:AI:PromptUsage:ConnectionString"] = "Data Source=usage.db",
            })
            .Build();

        // Act
        var aiConfig = config.GetSection("AppConfig:AI").Get<AIConfig>();

        // Assert
        aiConfig.Should().NotBeNull();
        aiConfig!.PromptUsage.PersistenceEnabled.Should().BeTrue();
        aiConfig.PromptUsage.ConnectionString.Should().Be("Data Source=usage.db");
    }

    [Fact]
    public void FallbackProviderConfig_BindsCapabilities()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FallbackChain:0:ClientType"] = "AzureOpenAI",
                ["FallbackChain:0:DeploymentId"] = "gpt-4o",
                ["FallbackChain:0:Capabilities:SupportsToolCalling"] = "true",
                ["FallbackChain:0:Capabilities:SupportsStreaming"] = "true",
                ["FallbackChain:0:Capabilities:SupportsVision"] = "true",
                ["FallbackChain:0:Capabilities:MaxTokens"] = "128000",
            })
            .Build();

        var entries = config.GetSection("FallbackChain").Get<FallbackProviderConfig[]>();

        entries.Should().NotBeNull().And.HaveCount(1);
        var entry = entries![0];
        entry.ClientType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
        entry.DeploymentId.Should().Be("gpt-4o");
        entry.Capabilities.SupportsToolCalling.Should().BeTrue();
        entry.Capabilities.SupportsVision.Should().BeTrue();
        entry.Capabilities.MaxTokens.Should().Be(128000);
    }
}

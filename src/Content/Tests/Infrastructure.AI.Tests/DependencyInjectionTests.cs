using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Resilience;
using Domain.Common.Config;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.KnowledgeGraph;
using Infrastructure.AI.Resilience;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests;

public sealed class DependencyInjectionTests
{
    private static ServiceCollection CreateBaseServices(AppConfig? appConfig = null)
    {
        var config = appConfig ?? new AppConfig();
        var services = new ServiceCollection();

        // Register dependencies that Infrastructure.AI expects
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptionsMonitor<AppConfig>>(
            new OptionsMonitorStub(config));
        // Infrastructure.AI consumers (MCP connection manager, plugin loader) read the
        // AppConfig:AI section bound as IOptionsMonitor<AIConfig>. The real composition root
        // registers both monitors; mirror that here so hosted-service enumeration can resolve.
        services.AddSingleton<IOptionsMonitor<Domain.Common.Config.AI.AIConfig>>(
            new AIConfigMonitorStub(config.AI));
        services.AddSingleton<ISender>(new Mock<ISender>().Object);
        services.AddKnowledgeGraphDependencies(config);

        return services;
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIChatClientFactory()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetService<IChatClientFactory>();

        factory.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIToolPermissionService()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var permissionService = provider.GetService<IToolPermissionService>();

        permissionService.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersISkillMetadataRegistry()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetService<ISkillMetadataRegistry>();

        registry.Should().NotBeNull();
    }

    [Fact]
    public void RegisterAIClients_UnconfiguredConfig_DoesNotRegisterAnyClients()
    {
        var config = new AppConfig(); // ApiKey is null => IsConfigured = false
        var services = CreateBaseServices(config);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        // With unconfigured AgentFramework, neither AzureOpenAIClient nor OpenAIClient should be registered
        var factory = provider.GetRequiredService<IChatClientFactory>();
        factory.IsAvailable(Domain.Common.Config.AI.AIAgentFrameworkClientType.AzureOpenAI).Should().BeFalse();
        factory.IsAvailable(Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI).Should().BeFalse();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIEscalationService()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var svc = provider.GetService<IEscalationService>();

        svc.Should().NotBeNull().And.BeOfType<DefaultEscalationService>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIEscalationAuditStore()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var store = provider.GetService<IEscalationAuditStore>();

        store.Should().NotBeNull().And.BeOfType<JsonlEscalationAuditStore>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIEscalationNotifier()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var notifier = provider.GetService<IEscalationNotifier>();

        notifier.Should().NotBeNull().And.BeOfType<CompositeEscalationNotifier>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersNotificationChannels()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var channels = provider.GetServices<IEscalationNotificationChannel>().ToList();

        channels.Should().Contain(c => c is NoOpSlackNotifier);
        channels.Should().Contain(c => c is NoOpTeamsNotifier);
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIProviderHealthMonitor()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var monitor = provider.GetService<IProviderHealthMonitor>();

        monitor.Should().NotBeNull().And.BeOfType<PollyProviderHealthMonitor>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIResilientChatClientProvider()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var resilientProvider = provider.GetService<IResilientChatClientProvider>();

        resilientProvider.Should().NotBeNull().And.BeOfType<ResilientChatClientProvider>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_ResilienceEnabled_RegistersLlmRetryQueueHostedService()
    {
        var config = new AppConfig { AI = { Resilience = new ResilienceConfig { Enabled = true } } };
        var services = CreateBaseServices(config);
        services.AddSingleton(TimeProvider.System);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();

        hostedServices.Should().Contain(s => s is LlmRetryQueue);
    }

    [Fact]
    public void AddInfrastructureAIDependencies_ResilienceDisabled_DoesNotRegisterLlmRetryQueueHostedService()
    {
        var config = new AppConfig { AI = { Resilience = new ResilienceConfig { Enabled = false } } };
        var services = CreateBaseServices(config);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();

        hostedServices.Should().NotContain(s => s is LlmRetryQueue);
    }

    [Fact]
    public void AddInfrastructureAIDependencies_CompositeNotifier_DoesNotContainItself()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(new AppConfig());
        using var provider = services.BuildServiceProvider();

        var channels = provider.GetServices<IEscalationNotificationChannel>().ToList();

        channels.Should().NotContain(c => c is CompositeEscalationNotifier);
    }

    /// <summary>
    /// Minimal IOptionsMonitor stub that returns a fixed value.
    /// </summary>
    private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
    {
        public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }

    /// <summary>
    /// Minimal IOptionsMonitor stub for the AppConfig:AI section bound as AIConfig.
    /// </summary>
    private sealed class AIConfigMonitorStub : IOptionsMonitor<Domain.Common.Config.AI.AIConfig>
    {
        public AIConfigMonitorStub(Domain.Common.Config.AI.AIConfig value) => CurrentValue = value;
        public Domain.Common.Config.AI.AIConfig CurrentValue { get; }
        public Domain.Common.Config.AI.AIConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<Domain.Common.Config.AI.AIConfig, string?> listener) => null;
    }
}

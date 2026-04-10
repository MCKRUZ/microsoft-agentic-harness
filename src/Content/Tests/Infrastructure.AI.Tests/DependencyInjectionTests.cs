using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        services.AddSingleton<IOptionsMonitor<AppConfig>>(
            new OptionsMonitorStub(config));

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
}
